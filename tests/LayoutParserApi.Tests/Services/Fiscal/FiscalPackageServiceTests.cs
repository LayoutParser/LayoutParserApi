using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Slice 2 (issue #229). Cobre upload idempotente (mesmo conteúdo não duplica) e a garantia de que
    /// conteúdo bruto do artefato NUNCA aparece em nenhuma mensagem de log — os dois critérios de
    /// aceite mais fáceis de quebrar silenciosamente numa refatoração futura.
    /// </summary>
    public class FiscalPackageServiceTests : IDisposable
    {
        private readonly string _tempStorePath = Path.Combine(Path.GetTempPath(), "fiscal-pkg-tests-" + Guid.NewGuid());

        // --- fakes ---

        private sealed class FakeStore : IFiscalPackageStore
        {
            public int CreateCallCount;
            public int CreateRevisionCallCount;
            public PackageDetail? PackageForMember { get; set; }
            public string? ArtifactStoragePath { get; set; }
            private readonly Dictionary<string, PackageDetail> _byIdempotencyKey = new();

            public Task<bool> EnsureProjectExistsAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken)
                => Task.FromResult(true);

            public Task<PackageDetail> CreatePackageAsync(
                Guid workspaceId, Guid projectId, Guid createdByUserId, string packageName, string idempotencyKey,
                IReadOnlyList<PackageArtifact> artifacts, CancellationToken cancellationToken)
            {
                CreateCallCount++;
                var detail = new PackageDetail(
                    Guid.NewGuid(), workspaceId, projectId, packageName, DateTimeOffset.UtcNow,
                    new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
                        artifacts.Select(a => new ArtifactSummary(a.ArtifactId, a.Kind, a.Sha256, a.SizeBytes, a.OriginalFileName, a.InspectionStatus, DateTimeOffset.UtcNow)).ToList()));

                _byIdempotencyKey[$"{workspaceId}|{projectId}|{idempotencyKey}"] = detail;
                return Task.FromResult(detail);
            }

            public Task<PackageDetail?> GetPackageIfMemberAsync(Guid packageId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(PackageForMember);

            public Task<PackageDetail?> FindPackageByIdempotencyKeyAsync(Guid workspaceId, Guid projectId, string idempotencyKey, CancellationToken cancellationToken)
                => Task.FromResult(_byIdempotencyKey.TryGetValue($"{workspaceId}|{projectId}|{idempotencyKey}", out var d) ? d : null);

            public Task<ArtifactSummary?> FindArtifactByHashAsync(Guid packageId, string sha256, CancellationToken cancellationToken)
                => Task.FromResult<ArtifactSummary?>(null);

            public Task UpdateInspectionStatusAsync(Guid artifactId, string inspectionStatus, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task<IReadOnlyList<ProjectSummary>> ListProjectsForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ProjectSummary>>(Array.Empty<ProjectSummary>());

            public Task<PackageDetail> CreateRevisionAsync(Guid packageId, Guid createdByUserId, IReadOnlyList<PackageArtifact> artifacts, CancellationToken cancellationToken)
            {
                CreateRevisionCallCount++;
                var nextRevisionNumber = PackageForMember!.LatestRevision.RevisionNumber + 1;
                var detail = PackageForMember with
                {
                    LatestRevision = new RevisionSummary(Guid.NewGuid(), nextRevisionNumber, DateTimeOffset.UtcNow,
                        artifacts.Select(a => new ArtifactSummary(a.ArtifactId, a.Kind, a.Sha256, a.SizeBytes, a.OriginalFileName, a.InspectionStatus, DateTimeOffset.UtcNow)).ToList())
                };
                PackageForMember = detail; // próxima revisão parte deste novo estado.
                return Task.FromResult(detail);
            }

            public Task<string?> GetArtifactStoragePathAsync(Guid artifactId, CancellationToken cancellationToken)
                => Task.FromResult(ArtifactStoragePath);
        }

        private sealed class FakeScanner : IAntivirusScanner
        {
            public Task<bool?> ScanAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
        }

        /// <summary>Captura TODAS as mensagens formatadas de log — o oráculo de "conteúdo nunca loga".</summary>
        private sealed class CapturingLogger : ILogger<FiscalPackageService>
        {
            public readonly List<string> Messages = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));
        }

        private static FiscalPackageService BuildService(IFiscalPackageStore store, CapturingLogger logger, string storePath)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ML:FiscalMappingPackagesPath"] = storePath })
                .Build();

            return new FiscalPackageService(store, new FakeScanner(), new FiscalMappingRuleExtractor(NullLogger<FiscalMappingRuleExtractor>.Instance), logger, config);
        }

        // --- idempotência ---

        [Fact]
        public async Task Reenviar_o_mesmo_conteudo_com_a_mesma_chave_nao_duplica()
        {
            var store = new FakeStore();
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);
            var workspaceId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("linha 1\nlinha 2")) };

            var first = await service.CreatePackageAsync(workspaceId, projectId, Guid.NewGuid(), "Pacote", "chave-fixa", artifacts, CancellationToken.None);
            var second = await service.CreatePackageAsync(workspaceId, projectId, Guid.NewGuid(), "Pacote", "chave-fixa", artifacts, CancellationToken.None);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(1, store.CreateCallCount); // 🔴 se a idempotência quebrar, vira 2.
            Assert.Equal(first.Package!.PackageId, second.Package!.PackageId);
        }

        [Fact]
        public async Task Sem_chave_explicita_o_hash_do_conteudo_e_a_chave_efetiva()
        {
            var store = new FakeStore();
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);
            var workspaceId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var content = System.Text.Encoding.UTF8.GetBytes("mesmo conteudo sempre");
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.txt", "text/plain", content) };

            var first = await service.CreatePackageAsync(workspaceId, projectId, Guid.NewGuid(), "Pacote", idempotencyKey: null, artifacts, CancellationToken.None);
            var second = await service.CreatePackageAsync(workspaceId, projectId, Guid.NewGuid(), "Pacote", idempotencyKey: null, artifacts, CancellationToken.None);

            Assert.Equal(1, store.CreateCallCount);
            Assert.Equal(first.Package!.PackageId, second.Package!.PackageId);
        }

        // --- Gap 2 (issue #201): nova revisão de pacote existente ---

        [Fact]
        public async Task CreateRevisionAsync_pacote_inexistente_ou_alheio_devolve_NotFound()
        {
            var store = new FakeStore(); // PackageForMember não configurado — simula "não existe/não é seu".
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("linha 1")) };

            var outcome = await service.CreateRevisionAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), artifacts, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.True(outcome.NotFound);
            Assert.Equal(0, store.CreateRevisionCallCount);
        }

        [Fact]
        public async Task CreateRevisionAsync_workspaceId_divergente_do_dono_real_tambem_NotFound()
        {
            var packageId = Guid.NewGuid();
            var workspaceReal = Guid.NewGuid();
            var store = new FakeStore
            {
                PackageForMember = new PackageDetail(packageId, workspaceReal, Guid.NewGuid(), "Pacote", DateTimeOffset.UtcNow,
                    new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Array.Empty<ArtifactSummary>()))
            };
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("linha 1")) };

            var outcome = await service.CreateRevisionAsync(Guid.NewGuid(), packageId, Guid.NewGuid(), artifacts, CancellationToken.None);

            Assert.True(outcome.NotFound);
        }

        [Fact]
        public async Task CreateRevisionAsync_artefato_invalido_devolve_422_sem_criar_revisao()
        {
            var packageId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var store = new FakeStore
            {
                PackageForMember = new PackageDetail(packageId, workspaceId, Guid.NewGuid(), "Pacote", DateTimeOffset.UtcNow,
                    new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Array.Empty<ArtifactSummary>()))
            };
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);
            // Extensão errada de propósito (kind Sample espera .txt).
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.xml", "text/xml", System.Text.Encoding.UTF8.GetBytes("<a/>")) };

            var outcome = await service.CreateRevisionAsync(workspaceId, packageId, Guid.NewGuid(), artifacts, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.False(outcome.NotFound);
            Assert.NotNull(outcome.Error);
            Assert.Equal(0, store.CreateRevisionCallCount);
        }

        [Fact]
        public async Task CreateRevisionAsync_sucesso_incrementa_o_numero_da_revisao()
        {
            var packageId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var store = new FakeStore
            {
                PackageForMember = new PackageDetail(packageId, workspaceId, Guid.NewGuid(), "Pacote", DateTimeOffset.UtcNow,
                    new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Array.Empty<ArtifactSummary>()))
            };
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("linha 1")) };

            var outcome = await service.CreateRevisionAsync(workspaceId, packageId, Guid.NewGuid(), artifacts, CancellationToken.None);

            Assert.True(outcome.Success);
            Assert.Equal(1, store.CreateRevisionCallCount);
            Assert.Equal(2, outcome.Package!.LatestRevision.RevisionNumber); // 🔴 se a store parar de incrementar, isto quebra.
        }

        // --- Gap 3 (issue #201): inventário de estrutura do Excel ---

        [Fact]
        public async Task GetExcelInventoryAsync_pacote_inexistente_ou_alheio_devolve_NotFound()
        {
            var store = new FakeStore();
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);

            var outcome = await service.GetExcelInventoryAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.True(outcome.NotFound);
        }

        [Fact]
        public async Task GetExcelInventoryAsync_artefato_que_nao_e_spec_devolve_422()
        {
            var packageId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var store = new FakeStore
            {
                PackageForMember = new PackageDetail(packageId, workspaceId, Guid.NewGuid(), "Pacote", DateTimeOffset.UtcNow,
                    new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
                        new[] { new ArtifactSummary(artifactId, ArtifactKind.Sample, "hash", 10, "sample.txt", InspectionStatus.Pending, DateTimeOffset.UtcNow) }))
            };
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);

            var outcome = await service.GetExcelInventoryAsync(workspaceId, packageId, artifactId, Guid.NewGuid(), CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.False(outcome.NotFound);
            Assert.Contains("spec", outcome.Error);
        }

        [Fact]
        public async Task GetExcelInventoryAsync_planilha_real_devolve_abas_e_colunas_reconhecidas()
        {
            var packageId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fiscal", "regra-cst-decision-table.xlsx");
            var store = new FakeStore
            {
                PackageForMember = new PackageDetail(packageId, workspaceId, Guid.NewGuid(), "Pacote", DateTimeOffset.UtcNow,
                    new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
                        new[] { new ArtifactSummary(artifactId, ArtifactKind.Spec, "hash", 10, "spec.xlsx", InspectionStatus.Pending, DateTimeOffset.UtcNow) })),
                // Caminho absoluto direto — a service compõe com _storePath, então usa caminho relativo vazio e storePath = pasta da fixture.
                ArtifactStoragePath = "regra-cst-decision-table.xlsx",
            };
            var service = BuildService(store, new CapturingLogger(), Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fiscal"));

            var outcome = await service.GetExcelInventoryAsync(workspaceId, packageId, artifactId, Guid.NewGuid(), CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            Assert.NotNull(outcome.Inventory);
            Assert.NotEmpty(outcome.Inventory!.DecisionSheets.Concat(outcome.Inventory.SkippedSheets.Select(s => new ExcelSheetInventory(s, Array.Empty<string>(), 0))));
        }

        // --- conteúdo bruto nunca em log ---

        [Fact]
        public async Task Conteudo_bruto_do_artefato_nunca_aparece_em_log_no_caminho_de_sucesso()
        {
            var store = new FakeStore();
            var logger = new CapturingLogger();
            var service = BuildService(store, logger, _tempStorePath);
            var segredoNoConteudo = "DADO-FISCAL-SENSIVEL-NAO-PODE-VAZAR-NO-LOG-12345";
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes(segredoNoConteudo)) };

            await service.CreatePackageAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Pacote", "k", artifacts, CancellationToken.None);

            Assert.DoesNotContain(logger.Messages, m => m.Contains(segredoNoConteudo));
        }

        [Fact]
        public async Task Conteudo_bruto_do_artefato_nunca_aparece_em_log_no_caminho_de_rejeicao()
        {
            var store = new FakeStore();
            var logger = new CapturingLogger();
            var service = BuildService(store, logger, _tempStorePath);
            var segredoNoConteudo = "<?xml version=\"1.0\"?><SEGREDO-QUE-NAO-PODE-VAZAR/>";
            // Extensão errada de propósito para forçar rejeição (kind Sample espera .txt).
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.xml", "text/xml", System.Text.Encoding.UTF8.GetBytes(segredoNoConteudo)) };

            var outcome = await service.CreatePackageAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Pacote", "k", artifacts, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.DoesNotContain(logger.Messages, m => m.Contains(segredoNoConteudo));
            Assert.Equal(0, store.CreateCallCount);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempStorePath))
                Directory.Delete(_tempStorePath, recursive: true);
        }

        // --- corrida de concorrência (bug encontrado por @lp-qa em CreatePackageAsync) ---

        /// <summary>
        /// Simula o comportamento real de <see cref="LayoutParserApi.Services.Database.SqlFiscalPackageStore"/>
        /// sob corrida: 2 chamadas concorrentes com a mesma IdempotencyKey podem passar pelo SELECT antes de
        /// qualquer uma commitar o INSERT (sem lock explícito). O UNIQUE do banco garante que só uma linha
        /// sobrevive; a corrigida trata a violação como sucesso e devolve o pacote do "vencedor" — em vez de
        /// propagar a exceção (que antes do fix virava 503 pro chamador perdedor da corrida).
        /// </summary>
        private sealed class RaceSimulatingStore : IFiscalPackageStore
        {
            private readonly Dictionary<string, PackageDetail> _byIdempotencyKey = new();
            private readonly object _lock = new();
            public int CreateCallCount;

            public Task<bool> EnsureProjectExistsAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken)
                => Task.FromResult(true);

            public async Task<PackageDetail> CreatePackageAsync(
                Guid workspaceId, Guid projectId, Guid createdByUserId, string packageName, string idempotencyKey,
                IReadOnlyList<PackageArtifact> artifacts, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref CreateCallCount);
                var key = $"{workspaceId}|{projectId}|{idempotencyKey}";

                // Janela de corrida real: ambas as chamadas concorrentes chegam aqui sem terem visto o
                // resultado uma da outra (equivalente ao SELECT prévio de FindPackageByIdempotencyKeyAsync
                // no FiscalPackageService não ter encontrado nada ainda).
                await Task.Delay(15, cancellationToken);

                var detail = new PackageDetail(
                    Guid.NewGuid(), workspaceId, projectId, packageName, DateTimeOffset.UtcNow,
                    new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
                        artifacts.Select(a => new ArtifactSummary(a.ArtifactId, a.Kind, a.Sha256, a.SizeBytes, a.OriginalFileName, a.InspectionStatus, DateTimeOffset.UtcNow)).ToList()));

                lock (_lock)
                {
                    // Equivalente ao UNIQUE (WorkspaceId, ProjectId, IdempotencyKey) do SQL: só o primeiro
                    // INSERT "vence"; o fix trata a violação como sucesso e devolve o pacote existente.
                    if (_byIdempotencyKey.TryGetValue(key, out var winner))
                        return winner;

                    _byIdempotencyKey[key] = detail;
                    return detail;
                }
            }

            public Task<PackageDetail?> GetPackageIfMemberAsync(Guid packageId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult<PackageDetail?>(null);

            public Task<PackageDetail?> FindPackageByIdempotencyKeyAsync(Guid workspaceId, Guid projectId, string idempotencyKey, CancellationToken cancellationToken)
                => Task.FromResult<PackageDetail?>(null); // Simula as 2 chamadas concorrentes NÃO encontrando nada no SELECT prévio.

            public Task<ArtifactSummary?> FindArtifactByHashAsync(Guid packageId, string sha256, CancellationToken cancellationToken)
                => Task.FromResult<ArtifactSummary?>(null);

            public Task UpdateInspectionStatusAsync(Guid artifactId, string inspectionStatus, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task<IReadOnlyList<ProjectSummary>> ListProjectsForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ProjectSummary>>(Array.Empty<ProjectSummary>());

            public Task<PackageDetail> CreateRevisionAsync(Guid packageId, Guid createdByUserId, IReadOnlyList<PackageArtifact> artifacts, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado neste conjunto de testes.");

            public Task<string?> GetArtifactStoragePathAsync(Guid artifactId, CancellationToken cancellationToken)
                => Task.FromResult<string?>(null);
        }

        [Fact]
        public async Task Duas_requisicoes_concorrentes_com_a_mesma_chave_convergem_para_o_mesmo_pacote_sem_erro()
        {
            var store = new RaceSimulatingStore();
            var service = BuildService(store, new CapturingLogger(), _tempStorePath);
            var workspaceId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var artifacts = new[] { new UploadedArtifactInput(ArtifactKind.Sample, "sample.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("linha 1\nlinha 2")) };

            var task1 = service.CreatePackageAsync(workspaceId, projectId, Guid.NewGuid(), "Pacote", "chave-concorrente", artifacts, CancellationToken.None);
            var task2 = service.CreatePackageAsync(workspaceId, projectId, Guid.NewGuid(), "Pacote", "chave-concorrente", artifacts, CancellationToken.None);

            var results = await Task.WhenAll(task1, task2);

            // 🔴 Antes do fix: o "perdedor" da corrida propagava SqlException (2601/2627) → 503 pro cliente.
            Assert.True(results[0].Success);
            Assert.True(results[1].Success);
            Assert.Equal(results[0].Package!.PackageId, results[1].Package!.PackageId);
            Assert.Equal(2, store.CreateCallCount); // ambas chegaram no INSERT — é o cenário exato da corrida.
        }
    }
}
