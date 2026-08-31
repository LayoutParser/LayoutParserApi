using System.Collections.Concurrent;

using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Identity
{
    /// <summary>
    /// Implementação <c>Scoped</c> de <see cref="IIdentityWorkspaceService"/>. Adiciona uma trava
    /// EM PROCESSO (por chave de identidade/usuário) por cima do UNIQUE constraint do SQL — a
    /// garantia definitiva contra duplicidade sob concorrência é do banco (multi-instância), mas o
    /// lock local evita disparar duas idas ao SQL no caso mais comum (duas requisições simultâneas
    /// batendo na mesma instância do processo).
    /// </summary>
    public sealed class IdentityWorkspaceService : IIdentityWorkspaceService
    {
        private readonly IIdentityWorkspaceStore _store;
        private readonly ILogger<IdentityWorkspaceService> _logger;

        // Estático de propósito: a garantia de "não duplicar" precisa valer entre requisições
        // concorrentes de instâncias Scoped diferentes, não só dentro de uma.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _identityLocks = new();
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _workspaceLocks = new();

        public IdentityWorkspaceService(IIdentityWorkspaceStore store, ILogger<IdentityWorkspaceService> logger)
        {
            _store = store;
            _logger = logger;
        }

        public async Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(subject))
                return null;

            var tenant = tenantOrIssuer ?? string.Empty;

            // ✅ subject NUNCA entra na chave de log nem é logado adiante — só compõe a chave do lock
            // em memória (nunca exposta).
            var lockKey = $"{provider}␟{tenant}␟{subject}";
            var gate = _identityLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken);
            try
            {
                return await _store.ResolveOrCreateUserAsync(provider, tenant, subject, cancellationToken);
            }
            catch (Exception ex)
            {
                // Fail-closed: identidade externa não resolvida vira UserId null. O chamador (endpoint)
                // nega acesso — nunca degrada para "segue sem filtro". Nunca logar subject.
                _logger.LogWarning(ex, "Falha ao resolver identidade externa (provider={Provider}, tenant={Tenant}) — negando UserId (fail-closed).", provider, tenant);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
        {
            var gate = _workspaceLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            WorkspaceSummary personal;
            try
            {
                personal = await _store.EnsurePersonalWorkspaceAsync(userId, cancellationToken);
            }
            finally
            {
                gate.Release();
            }

            var memberships = await _store.GetMembershipsAsync(userId, cancellationToken);
            return new WorkspaceMeResult(personal.WorkspaceId, memberships);
        }

        public Task<WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
            => _store.GetWorkspaceIfMemberAsync(workspaceId, userId, cancellationToken);
    }
}
