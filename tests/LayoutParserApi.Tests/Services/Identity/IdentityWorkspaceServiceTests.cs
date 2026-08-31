using LayoutParserApi.Services.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Tests.Services.Identity
{
    /// <summary>
    /// Slice 1 (issue #225/#228). Cobre os dois requisitos não-negociáveis do
    /// <see cref="IdentityWorkspaceService"/>: (1) duas requisições concorrentes do MESMO usuário novo
    /// não criam duas <c>ExternalIdentity</c>/dois workspaces pessoais (idempotência em processo — a
    /// garantia multi-instância é o UNIQUE constraint do <c>SqlIdentityWorkspaceStore</c>, não
    /// exercitável aqui sem SQL Server real); (2) falha na resolução degrada para <c>UserId</c> nulo
    /// (fail-closed), nunca lança nem loga o <c>subject</c>.
    /// </summary>
    public class IdentityWorkspaceServiceTests
    {
        // --- fakes ---

        /// <summary>
        /// Simula a store SQL real: primeira chamada para uma chave (provider,tenant,subject) "cria"
        /// (com um atraso proposital, para abrir a janela de corrida) e as demais reutilizam o mesmo
        /// UserId. <see cref="CreateCallCount"/> é o oráculo do teste — se a trava em processo do
        /// serviço falhar, mais de uma criação aconteceria.
        /// </summary>
        private sealed class RaceyFakeStore : IIdentityWorkspaceStore
        {
            private readonly Dictionary<string, Guid> _identities = new();
            private readonly object _gate = new();
            public int CreateCallCount;
            public int EnsurePersonalWorkspaceCreateCallCount;
            private readonly Dictionary<Guid, WorkspaceSummary> _personalWorkspaces = new();
            public Exception? ThrowOnResolve { get; set; }

            public async Task<Guid> ResolveOrCreateUserAsync(string provider, string tenantOrIssuer, string subject, CancellationToken cancellationToken)
            {
                if (ThrowOnResolve != null)
                    throw ThrowOnResolve;

                var key = $"{provider}|{tenantOrIssuer}|{subject}";

                Guid? existing;
                lock (_gate)
                    existing = _identities.TryGetValue(key, out var v) ? v : null;

                if (existing != null)
                    return existing.Value;

                // Janela de corrida proposital: se a trava em processo do IdentityWorkspaceService
                // não existisse, uma segunda chamada concorrente chegaria aqui ANTES do Add abaixo.
                await Task.Delay(20, cancellationToken);

                lock (_gate)
                {
                    if (_identities.TryGetValue(key, out var raced))
                        return raced; // outra thread já criou (não deveria acontecer com a trava do serviço)

                    var newId = Guid.NewGuid();
                    _identities[key] = newId;
                    Interlocked.Increment(ref CreateCallCount);
                    return newId;
                }
            }

            public async Task<WorkspaceSummary> EnsurePersonalWorkspaceAsync(Guid userId, CancellationToken cancellationToken)
            {
                lock (_gate)
                {
                    if (_personalWorkspaces.TryGetValue(userId, out var existing))
                        return existing;
                }

                await Task.Delay(20, cancellationToken);

                lock (_gate)
                {
                    if (_personalWorkspaces.TryGetValue(userId, out var raced))
                        return raced;

                    var summary = new WorkspaceSummary(Guid.NewGuid(), "Meu workspace fiscal", "personal", "owner", DateTimeOffset.UtcNow);
                    _personalWorkspaces[userId] = summary;
                    Interlocked.Increment(ref EnsurePersonalWorkspaceCreateCallCount);
                    return summary;
                }
            }

            public Task<IReadOnlyList<WorkspaceSummary>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken)
            {
                lock (_gate)
                {
                    IReadOnlyList<WorkspaceSummary> list = _personalWorkspaces.TryGetValue(userId, out var w)
                        ? new List<WorkspaceSummary> { w }
                        : new List<WorkspaceSummary>();
                    return Task.FromResult(list);
                }
            }

            public Task<WorkspaceSummary?> GetWorkspaceIfMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado por estes testes.");
        }

        /// <summary>Captura mensagens formatadas de log para o teste "subject nunca vaza".</summary>
        private sealed class CapturingLogger : ILogger<IdentityWorkspaceService>
        {
            public List<string> Messages { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        // --- idempotência sob concorrência ---

        [Fact]
        public async Task ResolveOrCreateUserAsync_duas_chamadas_concorrentes_mesmo_usuario_retornam_o_mesmo_userId_e_criam_uma_unica_vez()
        {
            var store = new RaceyFakeStore();
            var service = new IdentityWorkspaceService(store, new CapturingLogger());

            var tasks = Enumerable.Range(0, 10)
                .Select(_ => service.ResolveOrCreateUserAsync("entra", "tenant-x", "sub-mesma-pessoa", CancellationToken.None))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.Equal(results[0], r));
            Assert.Equal(1, store.CreateCallCount);
        }

        [Fact]
        public async Task ResolveOrCreateUserAsync_usuarios_diferentes_nao_colidem()
        {
            var store = new RaceyFakeStore();
            var service = new IdentityWorkspaceService(store, new CapturingLogger());

            var userA = await service.ResolveOrCreateUserAsync("entra", "tenant-x", "sub-alice", CancellationToken.None);
            var userB = await service.ResolveOrCreateUserAsync("entra", "tenant-x", "sub-bob", CancellationToken.None);

            Assert.NotEqual(userA, userB);
            Assert.Equal(2, store.CreateCallCount);
        }

        [Fact]
        public async Task GetOrCreateMyWorkspacesAsync_duas_chamadas_concorrentes_nao_duplicam_workspace_pessoal()
        {
            var store = new RaceyFakeStore();
            var service = new IdentityWorkspaceService(store, new CapturingLogger());
            var userId = Guid.NewGuid();

            var tasks = Enumerable.Range(0, 10)
                .Select(_ => service.GetOrCreateMyWorkspacesAsync(userId, CancellationToken.None))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.Equal(results[0].ActiveWorkspaceId, r.ActiveWorkspaceId));
            Assert.Equal(1, store.EnsurePersonalWorkspaceCreateCallCount);
        }

        // --- fail-closed ---

        [Fact]
        public async Task ResolveOrCreateUserAsync_falha_no_store_retorna_null_sem_lancar()
        {
            var store = new RaceyFakeStore { ThrowOnResolve = new InvalidOperationException("SQL fora do ar") };
            var service = new IdentityWorkspaceService(store, new CapturingLogger());

            var result = await service.ResolveOrCreateUserAsync("entra", "tenant-x", "sub-qualquer", CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public async Task ResolveOrCreateUserAsync_dados_de_entrada_invalidos_retorna_null_sem_tocar_o_store()
        {
            var store = new RaceyFakeStore();
            var service = new IdentityWorkspaceService(store, new CapturingLogger());

            var semProvider = await service.ResolveOrCreateUserAsync("", "tenant-x", "sub", CancellationToken.None);
            var semSubject = await service.ResolveOrCreateUserAsync("entra", "tenant-x", "", CancellationToken.None);

            Assert.Null(semProvider);
            Assert.Null(semSubject);
            Assert.Equal(0, store.CreateCallCount);
        }

        // --- subject nunca aparece em log ---

        [Fact]
        public async Task ResolveOrCreateUserAsync_falha_loga_sem_expor_o_subject()
        {
            const string subjectSecreto = "sub-1234-nao-pode-vazar";
            var store = new RaceyFakeStore { ThrowOnResolve = new InvalidOperationException("SQL fora do ar") };
            var logger = new CapturingLogger();
            var service = new IdentityWorkspaceService(store, logger);

            await service.ResolveOrCreateUserAsync("entra", "tenant-x", subjectSecreto, CancellationToken.None);

            Assert.NotEmpty(logger.Messages);
            Assert.DoesNotContain(logger.Messages, m => m.Contains(subjectSecreto, StringComparison.Ordinal));
        }
    }
}
