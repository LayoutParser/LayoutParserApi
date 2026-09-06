using System.Diagnostics;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #104: teste ponta a ponta de <c>TryEnqueueAiCandidate</c> exercitando
    /// <c>ExecuteTransformationCandidates</c> PÚBLICO (não via reflection do método privado, como
    /// <see cref="TransformationExecutionControllerUserIsolationTests"/> faz por documentar essa
    /// mesma limitação). A peça que faltava era um double do runner x86 do pathway sysmiddle — este
    /// arquivo introduz um (<see cref="FakeSysmiddleRunner"/>), no MESMO ponto de substituição já
    /// usado por <c>LowCodeRunnerCancellationTests</c>
    /// (<c>LowCodeTransformationService.ExecuteRunnerProcessAsync</c>, <c>protected virtual</c> de
    /// propósito): tudo que roda de verdade é o código de produção (semáforo, args, leitura do
    /// arquivo de saída) — só o <c>Process.Start</c> do <c>.exe</c> x86 é substituído, porque ele não
    /// existe na máquina de teste e não é ele que está sob julgamento.
    ///
    /// <para>Cobertura fechada: o fluxo real (<c>ExecuteTransformationCandidates</c> →
    /// <c>ExecuteSysmiddleCandidatesAsync</c> → <c>LowCodeAutoTransformationService.RunAsync</c> →
    /// <c>LowCodeTransformationService.TransformAsync</c> → runner double → gabarito sysmiddle →
    /// <c>AiCandidateDispatchPlan.TryBuild</c> → <c>TryEnqueueAiCandidate</c> →
    /// <c>IAiTransformationCandidateService.EnqueueAsync</c>) propaga o <c>userId</c> correto — a
    /// mesma classe de regressão que o <c>@lp-qa</c> demonstrou (CurrentUserId virar um valor fixo),
    /// mas agora coberta ponta a ponta em vez de só no call-site isolado por reflection.</para>
    /// </summary>
    public class TransformationExecutionControllerEndToEndAiCandidateTests : IDisposable
    {
        private readonly string _tempStorePath =
            Path.Combine(Path.GetTempPath(), "lp-tests-e2e-ai-candidate", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_tempStorePath)) Directory.Delete(_tempStorePath, recursive: true); }
            catch { /* best effort — mesmo padrão do TryDelete de produção */ }
        }

        [Fact]
        public async Task ExecuteTransformationCandidates_ponta_a_ponta_propaga_CurrentUserId_ate_EnqueueAsync()
        {
            // --- Arrange: layout + mapper "existentes", runner x86 substituído pelo double ---

            var layoutGuid = Guid.NewGuid();
            var layoutName = "LAYOUT_TESTE_E2E";
            var layoutRecord = new LayoutRecord { LayoutGuid = layoutGuid, Name = layoutName };

            var fakeLayoutDb = new FakeLayoutDatabaseService(layoutRecord);

            var mapper = new Mapper
            {
                MapperGuid = "mapper-e2e-1",
                Name = "Mapper E2E",
                PackageGuid = "pkg-e2e",
                ProjectId = "2",
                TargetLayoutGuid = layoutGuid.ToString()
            };
            var fakeMapperDb = new FakeMapperDatabaseService(new List<Mapper> { mapper });

            var runnerOptions = Options.Create(new LowCodeRunnerOptions
            {
                // Não-vazios só para passar nas validações de TransformAsync — o processo nunca é
                // iniciado de verdade (ver FakeSysmiddleRunner).
                RunnerPath = "runner-inexistente.exe",
                SysmiddleDir = Path.GetTempPath(),
                GlobalFolder = Path.GetTempPath(),
                Package = "pkg-teste"
            });

            var runnerConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:File:Directory"] = Path.Combine(_tempStorePath, "runner-logs")
                })
                .Build();

            var fakeRunner = new FakeSysmiddleRunner(runnerOptions, runnerConfig, gabaritoXml: "<xml>gabarito-e2e</xml>");

            var storeConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LowCodeTransformationsPath"] = _tempStorePath
                })
                .Build();
            var store = new LowCodeTransformationStore(
                NullLogger<LowCodeTransformationStore>.Instance,
                storeConfig,
                runnerOptions,
                redis: null);

            // O container só existe para dar ao LowCodeAutoTransformationService um
            // IServiceScopeFactory de onde resolver MapperDatabaseService dentro do próprio escopo —
            // mesmo padrão de produção (Program.cs: Singleton injeta IServiceScopeFactory, nunca o
            // serviço Scoped direto).
            var scopeServices = new ServiceCollection();
            scopeServices.AddScoped<MapperDatabaseService>(_ => fakeMapperDb);
            using var scopeProvider = scopeServices.BuildServiceProvider();

            var lowCodeAuto = new LowCodeAutoTransformationService(
                NullLogger<LowCodeAutoTransformationService>.Instance,
                scopeProvider.GetRequiredService<IServiceScopeFactory>(),
                fakeRunner,
                store,
                runnerOptions);

            var spy = new SpyAiCandidateService();
            var user = new FakeCurrentUser { Name = "erika" };

            var controller = new TransformationExecutionController(
                NullLogger<TransformationExecutionController>.Instance,
                pipelineService: null!, // tcl-xsl fica de fora — NullReferenceException é capturada pelo catch do pathway (ver ExecuteTclXslCandidatesAsync)
                validatorService: null!,
                learningService: null!,
                autoGenerator: null!,
                lowCode: fakeRunner,
                lowCodeAuto: lowCodeAuto,
                layoutDb: fakeLayoutDb,
                lowCodeOptions: runnerOptions,
                aiCandidateService: spy,
                aiFallbackGate: new SpyAiFallbackSuppressionGate(),
                aiUserInstructionStore: new AiUserInstructionStore(),
                aiUserSessionStore: new SqlAiUserSessionStore(
                    NullLogger<SqlAiUserSessionStore>.Instance,
                    new ConfigurationBuilder().Build(),
                    Options.Create(new AiUserSessionHistoryOptions())),
                currentUser: user,
                mapperDb: null!,
                layoutParser: null!,
                fieldMappingComposition: null!,
                scopeFactory: scopeProvider.GetRequiredService<IServiceScopeFactory>(),
                canaryAlert: new LayoutParserApi.Services.Security.CanaryAlertService(
                    NullLogger<LayoutParserApi.Services.Security.CanaryAlertService>.Instance));

            var request = new TransformationRequest
            {
                InputContent = "linha-posicional-de-entrada-e2e",
                LayoutName = layoutName,
                LayoutGuid = layoutGuid.ToString()
            };

            // --- Act: chama o endpoint PÚBLICO de verdade, sem reflection ---

            var actionResult = await controller.ExecuteTransformationCandidates(request);

            // --- Assert: o pathway sysmiddle rodou de ponta a ponta pelo double ... ---

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            dynamic payload = ok.Value!;
            var candidatesJson = System.Text.Json.JsonSerializer.Serialize(payload);
            Assert.Contains("sysmiddle", candidatesJson);
            Assert.Contains("gabarito-e2e", candidatesJson);
            Assert.True(fakeRunner.FoiInvocado, "o double do runner x86 nunca foi chamado — o pathway sysmiddle não rodou");

            // ... e o TryEnqueueAiCandidate (fire-and-forget) propagou o CurrentUserId real, não um
            // valor fixo — a mesma regressão que o @lp-qa demonstrou por mutação.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (spy.LastEnqueueUserId == null && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Equal("erika", spy.LastEnqueueUserId);
        }

        // ─────────────────────────────── fakes/doubles ───────────────────────────────

        /// <summary>
        /// Double do runner x86 do pathway sysmiddle (issue #104). Mesmo ponto de substituição do
        /// <c>RunnerBloqueado</c> em <c>LowCodeRunnerCancellationTests</c>
        /// (<c>ExecuteRunnerProcessAsync</c>), mas aqui simulando SUCESSO: escreve o XML gabarito no
        /// <c>--outputFile</c> que o código real de <c>TransformAsync</c> já preparou e devolve
        /// exit=0 — para além dele, todo o resto do ciclo real (semáforo, leitura do arquivo de
        /// saída, limpeza dos temporários) roda sem alteração.
        /// </summary>
        private sealed class FakeSysmiddleRunner : LowCodeTransformationService
        {
            private readonly string _gabaritoXml;
            private volatile bool _foiInvocado;

            public FakeSysmiddleRunner(IOptions<LowCodeRunnerOptions> options, IConfiguration configuration, string gabaritoXml)
                : base(NullLogger<LowCodeTransformationService>.Instance, options, configuration)
            {
                _gabaritoXml = gabaritoXml;
            }

            public bool FoiInvocado => _foiInvocado;

            protected override async Task<LowCodeRunnerExecution> ExecuteRunnerProcessAsync(
                ProcessStartInfo psi,
                int timeoutSeconds,
                string correlationId,
                string? mapperId,
                string? mapperName,
                CancellationToken cancellationToken)
            {
                _foiInvocado = true;

                var args = psi.ArgumentList;
                var idx = args.IndexOf("--outputFile");
                if (idx >= 0 && idx + 1 < args.Count)
                {
                    await File.WriteAllTextAsync(args[idx + 1], _gabaritoXml, cancellationToken);
                }

                return new LowCodeRunnerExecution(0, "", "");
            }
        }

        /// <summary>Double do catálogo: só resolve o layout que os testes precisam.</summary>
        private sealed class FakeLayoutDatabaseService : ILayoutDatabaseService
        {
            private readonly LayoutRecord _layout;

            public FakeLayoutDatabaseService(LayoutRecord layout) => _layout = layout;

            public Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request) =>
                Task.FromResult(new LayoutSearchResponse
                {
                    Success = true,
                    Layouts = new List<LayoutRecord> { _layout }
                });

            public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => Task.FromResult<LayoutRecord?>(_layout);
        }

        /// <summary>
        /// Double do banco de mapeadores: só sobrescreve o método <c>virtual</c> usado pelo pathway
        /// sysmiddle (mesmo ponto de substituição documentado no construtor da classe base —
        /// <c>MapperDatabaseService</c> não tem interface própria, então o double é por herança).
        /// </summary>
        private sealed class FakeMapperDatabaseService : MapperDatabaseService
        {
            private readonly List<Mapper> _mappers;

            public FakeMapperDatabaseService(List<Mapper> mappers)
                : base(NullLogger<MapperDatabaseService>.Instance, new FakeDecryptionService(), new ConfigurationBuilder().Build())
            {
                _mappers = mappers;
            }

            public override Task<List<Mapper>> GetRankedMapperCandidatesForLayoutGuidAsync(
                string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids) =>
                Task.FromResult(_mappers);
        }

        private sealed class FakeDecryptionService : IDecryptionService
        {
            public Task<string> DecryptContentAsync(string encryptedContent) => Task.FromResult(encryptedContent);
            public bool IsDecryptorAvailable => true;
        }

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
            public Guid? UserId => null;
        }

        /// <summary>Spy: captura o userId recebido no enqueue — mesmo papel do spy usado em
        /// <see cref="TransformationExecutionControllerUserIsolationTests"/>, reintroduzido aqui
        /// (arquivos distintos, sem depender de tipos internos de outro arquivo de teste).</summary>
        private sealed class SpyAiCandidateService : IAiTransformationCandidateService
        {
            public string? LastEnqueueUserId { get; private set; }

            public Task EnqueueAsync(
                string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
                string inputContent, string? groundTruthXml, CancellationToken cancellationToken,
                IReadOnlyList<Models.Entities.ParsedField>? parsedFields = null)
            {
                LastEnqueueUserId = userId;
                return Task.CompletedTask;
            }

            public Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken) =>
                Task.FromResult(new AiCandidateStatus { Status = AiCandidateStatus.StatusNotFound });
        }

        private sealed class SpyAiFallbackSuppressionGate : IAiFallbackSuppressionGate
        {
            public bool IsInCooldown(Guid layoutGuid, out DateTimeOffset retryAt)
            {
                retryAt = default;
                return false;
            }

            public void RegisterFailure(Guid layoutGuid, TimeSpan cooldown) { }
            public void ClearCooldown(Guid layoutGuid) { }
        }
    }
}
