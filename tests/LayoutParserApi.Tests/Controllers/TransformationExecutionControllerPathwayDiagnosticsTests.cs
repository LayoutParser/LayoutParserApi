using LayoutParserApi.Controllers;
using LayoutParserApi.Models;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue LayoutParserReact #86: <c>POST execute-candidates</c> passa a devolver
    /// <c>pathwayDiagnostics[]</c> populado (não mais sempre vazio) para os dois pathways síncronos
    /// (sysmiddle, tcl-xsl). Reproduz, com documento/layout SINTÉTICOS (nunca dado real de cliente),
    /// os dois cenários relatados na issue: "nenhum mapeador low-code" (sysmiddle) e "arquivo MAP não
    /// encontrado" (tcl-xsl) — e confirma que cada pathway termina em exatamente 1 diagnóstico, nunca
    /// silencioso.
    ///
    /// <para>Reaproveita o padrão de <c>TransformationPipelineServiceMapFileTests</c> (fixture mínima
    /// de pastas TCL/XSL vazias) e de <c>LowCodeAutoTransformationCacheTests</c> (fake de
    /// <see cref="MapperDatabaseService"/> devolvendo lista vazia — sem tocar runner/SQL real).</para>
    /// </summary>
    public class TransformationExecutionControllerPathwayDiagnosticsTests
    {
        private const string LayoutName = "LAY_SINTETICO_TESTE_ISSUE86";
        private static readonly Guid LayoutGuid = Guid.Parse("00000000-0000-0000-0000-0000000000e8");

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; } = "issue86-test-user";
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
            public Guid? UserId => null;
        }

        private sealed class FakeLayoutDatabaseService : ILayoutDatabaseService
        {
            public Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request) =>
                Task.FromResult(new LayoutSearchResponse
                {
                    Success = true,
                    Layouts = new List<LayoutRecord> { new() { Name = LayoutName, LayoutGuid = LayoutGuid } }
                });

            public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => Task.FromResult<LayoutRecord?>(null);
        }

        /// <summary>Mesmo papel do <c>MapperDbFalso</c> de <c>LowCodeAutoTransformationCacheTests</c>,
        /// mas sempre devolvendo lista vazia — força <c>autoResult.Applicable == false</c> (Estado A,
        /// "no_mapper") sem nunca tocar o runner x86 nem SQL real.</summary>
        private sealed class MapperDbVazio : MapperDatabaseService
        {
            public MapperDbVazio(IConfiguration config) : base(NullLogger<MapperDatabaseService>.Instance, null!, config) { }

            public override Task<List<Models.Entities.Mapper>> GetRankedMapperCandidatesForLayoutGuidAsync(
                string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids)
                => Task.FromResult(new List<Models.Entities.Mapper>());
        }

        private sealed class SpyAiCandidateService : IAiTransformationCandidateService
        {
            public int EnqueueCount { get; private set; }
            public Task EnqueueAsync(string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
                string inputContent, string? groundTruthXml, CancellationToken cancellationToken,
                IReadOnlyList<Models.Entities.ParsedField>? parsedFields = null)
            {
                EnqueueCount++;
                return Task.CompletedTask;
            }

            public Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken) =>
                Task.FromResult(new AiCandidateStatus { Status = AiCandidateStatus.StatusNotFound });
        }

        private sealed class SpyAiFallbackSuppressionGate : IAiFallbackSuppressionGate
        {
            public bool IsInCooldown(Guid layoutGuid, out DateTimeOffset retryAt) { retryAt = default; return false; }
            public void RegisterFailure(Guid layoutGuid, TimeSpan cooldown) { }
            public void ClearCooldown(Guid layoutGuid) { }
        }

        /// <summary>
        /// Constrói o controller real, com sysmiddle (<see cref="LowCodeAutoTransformationService"/>)
        /// apontando para um <see cref="MapperDbVazio"/> (sem mapper cadastrado) e tcl-xsl
        /// (<see cref="TransformationPipelineService"/>) apontando para uma pasta TCL temporária
        /// vazia (sem <c>.tcl</c> para o layout) — reproduz os dois sintomas originais da issue #86
        /// (candidates: [] + as duas mensagens de texto) com um payload 100% sintético.
        /// </summary>
        private static (TransformationExecutionController Controller, SpyAiCandidateService AiSpy, string TclDir) BuildController()
        {
            var raiz = Path.Combine(Path.GetTempPath(), "lp-tests", "execute-candidates-diag", Guid.NewGuid().ToString("N"));
            var tclDir = Path.Combine(raiz, "tcl");
            var xslDir = Path.Combine(raiz, "xsl");
            Directory.CreateDirectory(tclDir);
            Directory.CreateDirectory(xslDir);

            var lowCodeConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LowCodeTransformationsPath"] = Path.Combine(raiz, "lowcode-store"),
                    ["Logging:File:Directory"] = Path.Combine(raiz, "runner-logs")
                })
                .Build();

            var lowCodeOptions = Options.Create(new LowCodeRunnerOptions
            {
                RunnerPath = "runner-inexistente.exe",
                SysmiddleDir = Path.GetTempPath(),
                GlobalFolder = Path.GetTempPath()
            });

            var store = new LowCodeTransformationStore(
                NullLogger<LowCodeTransformationStore>.Instance, lowCodeConfig, lowCodeOptions, redis: null);

            var mapperDb = new MapperDbVazio(lowCodeConfig);
            var services = new ServiceCollection();
            services.AddScoped<MapperDatabaseService>(_ => mapperDb);

            // O runner (LowCodeTransformationService) nunca é chamado quando não há mapper — ver
            // LowCodeAutoTransformationService.TransformAndPersistAsync (checa ranked.Count == 0 ANTES
            // de tocar o runner). null! é seguro aqui pelo mesmo motivo documentado em
            // TransformationExecutionControllerUserIsolationTests.BuildController.
            var lowCodeAuto = new LowCodeAutoTransformationService(
                NullLogger<LowCodeAutoTransformationService>.Instance,
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                null!,
                store,
                lowCodeOptions);

            var pipelineConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TransformationPipeline:TclPath"] = tclDir,
                    ["TransformationPipeline:XslPath"] = xslDir,
                })
                .Build();
            var pipelineService = new TransformationPipelineService(
                NullLogger<TransformationPipelineService>.Instance, pipelineConfig);

            var aiSpy = new SpyAiCandidateService();

            var controller = new TransformationExecutionController(
                NullLogger<TransformationExecutionController>.Instance,
                pipelineService: pipelineService,
                validatorService: null!,
                learningService: null!,
                autoGenerator: null!,
                lowCode: null!,
                lowCodeAuto: lowCodeAuto,
                layoutDb: new FakeLayoutDatabaseService(),
                lowCodeOptions: lowCodeOptions,
                aiCandidateService: aiSpy,
                aiFallbackGate: new SpyAiFallbackSuppressionGate(),
                aiUserInstructionStore: new LayoutParserApi.Services.Transformation.Ai.AiUserInstructionStore(),
                currentUser: new FakeCurrentUser(),
                mapperDb: null!,
                layoutParser: null!,
                fieldMappingComposition: null!,
                scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

            return (controller, aiSpy, tclDir);
        }

        [Fact]
        public async Task Sem_mapper_e_sem_tcl_pathwayDiagnostics_reporta_no_mapper_e_map_not_found()
        {
            var (controller, aiSpy, _) = BuildController();

            var request = new TransformationRequest
            {
                InputContent = "000001DADOS POSICIONAIS SINTETICOS DE TESTE ISSUE86",
                LayoutName = LayoutName,
                LayoutGuid = LayoutGuid.ToString()
            };

            var actionResult = await controller.ExecuteTransformationCandidates(request);

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            Assert.Empty(response.Candidates);
            // CorrelationId pode ser null fora de um pipeline HTTP real (sem middleware de correlação
            // no teste) — o contrato exige a PROPRIEDADE presente no shape (response.CorrelationId
            // compila e existe), não um valor não-nulo neste cenário sem HttpContext.

            // 3 diagnósticos: sysmiddle (no_mapper) + tcl-xsl (map_not_found) + ai-fallback
            // (candidate_generated, ver asserção mais abaixo) — nenhum pathway fica silencioso.
            Assert.Equal(3, response.PathwayDiagnostics.Count);

            var sysmiddle = Assert.Single(response.PathwayDiagnostics, d => d.Pathway == "sysmiddle");
            Assert.Equal("not_applicable", sysmiddle.Status);
            Assert.Equal("no_mapper", sysmiddle.Code);
            Assert.False(string.IsNullOrWhiteSpace(sysmiddle.Message));

            var tclXsl = Assert.Single(response.PathwayDiagnostics, d => d.Pathway == "tcl-xsl");
            Assert.Equal("failed", tclXsl.Status);
            Assert.Equal("map_not_found", tclXsl.Code);
            Assert.False(string.IsNullOrWhiteSpace(tclXsl.Message));

            // Fallback de IA (Estado A: nenhum FailureKind.ExecutionInfraError, já que ambos os
            // pathways síncronos reportaram not_applicable/failed com códigos de "não encontrado", não
            // de infra) deve ter disparado — e ganha seu próprio 3º diagnóstico. Aqui o contrato do
            // desenho (§4.3) classifica map_not_found/xsl_not_found como FailureKind.NotApplicable no
            // controller, não ExecutionInfraError — então o fallback de IA continua elegível.
            Assert.Equal(1, aiSpy.EnqueueCount);
            var aiFallback = Assert.Single(response.PathwayDiagnostics, d => d.Pathway == "ai-fallback");
            Assert.Equal("candidate_generated", aiFallback.Status);
        }

        [Fact]
        public async Task Sem_mapper_e_sem_tcl_nenhuma_mensagem_vaza_caminho_de_disco()
        {
            var (controller, _, tclDir) = BuildController();

            var request = new TransformationRequest
            {
                InputContent = "000001OUTRO DOCUMENTO SINTETICO DE TESTE",
                LayoutName = LayoutName,
                LayoutGuid = LayoutGuid.ToString()
            };

            var actionResult = await controller.ExecuteTransformationCandidates(request);
            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            foreach (var diag in response.PathwayDiagnostics)
            {
                Assert.DoesNotContain(@"C:\", diag.Message ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(tclDir, diag.Message ?? "", StringComparison.OrdinalIgnoreCase);
            }
            foreach (var warning in response.Warnings)
            {
                Assert.DoesNotContain(@"C:\", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(tclDir, warning, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Xsl_ausente_apos_tcl_resolvido_reporta_xsl_not_found()
        {
            var (controller, _, tclDir) = BuildController();

            // Cria o .tcl (fixture mínima já usada em TransformationPipelineServiceMapFileTests) sem
            // criar nenhum .xsl correspondente — força o caminho "MAP resolvido, XSL não encontrado".
            var mapXml = "<MAP><LINE identifier=\"HEADER\" name=\"HEADER\"><FIELD name=\"data\" length=\"8\"/></LINE></MAP>";
            await File.WriteAllTextAsync(Path.Combine(tclDir, $"{LayoutName}.tcl"), mapXml);

            var request = new TransformationRequest
            {
                InputContent = "20260827SINTETICO",
                LayoutName = LayoutName,
                LayoutGuid = LayoutGuid.ToString()
            };

            var actionResult = await controller.ExecuteTransformationCandidates(request);
            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            var tclXsl = Assert.Single(response.PathwayDiagnostics, d => d.Pathway == "tcl-xsl");
            Assert.Equal("failed", tclXsl.Status);
            Assert.Equal("xsl_not_found", tclXsl.Code);
        }

        /// <summary>
        /// Gate QA (@lp-qa) — issue #138/#126: o contrato exige distinguir <c>null</c>
        /// ("pathway não suporta rastreabilidade") de <c>[]</c> ("suporta, mas não achou nada") em
        /// <see cref="TransformationCandidate.SectionMappings"/>. Os testes existentes de
        /// <c>SysmiddleSectionMappingResolverTests</c> já cobrem o caso <c>[]</c> do pathway
        /// sysmiddle; faltava um teste ponta-a-ponta do controller com um candidato tcl-xsl
        /// BEM-SUCEDIDO (não apenas os cenários de falha já cobertos acima) confirmando que
        /// <c>SectionMappings</c> sai <c>null</c> por definição — reaproveita o fixture mínimo de
        /// <c>TransformationPipelineServiceMapFileTests.Layout_real_CNHI_resolve_MAP_via_TclPath_layoutName_tcl</c>.
        /// </summary>
        [Fact]
        public async Task TclXsl_bem_sucedido_reporta_SectionMappings_null_nao_lista_vazia()
        {
            var (controller, _, tclDir) = BuildController();
            var xslDir = Path.Combine(Path.GetDirectoryName(tclDir)!, "xsl");

            var mapXml = "<MAP><LINE identifier=\"HEADER\" name=\"HEADER\"><FIELD name=\"data\" length=\"8\"/></LINE></MAP>";
            await File.WriteAllTextAsync(Path.Combine(tclDir, $"{LayoutName}.tcl"), mapXml);

            var xslContent =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
                "  <xsl:output method=\"xml\" encoding=\"UTF-8\"/>" +
                "  <xsl:template match=\"/\"><Resultado/></xsl:template>" +
                "</xsl:stylesheet>";
            await File.WriteAllTextAsync(Path.Combine(xslDir, $"MAP_TESTE_{LayoutName}.xsl"), xslContent);

            var request = new TransformationRequest
            {
                InputContent = "20260827SINTETICO",
                LayoutName = LayoutName,
                LayoutGuid = LayoutGuid.ToString()
            };

            var actionResult = await controller.ExecuteTransformationCandidates(request);
            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            var tclXslCandidate = Assert.Single(response.Candidates, c => c.Pathway == "tcl-xsl");
            Assert.Null(tclXslCandidate.SectionMappings);
            Assert.Null(tclXslCandidate.XmlNamespaces);

            var tclXslDiag = Assert.Single(response.PathwayDiagnostics, d => d.Pathway == "tcl-xsl");
            Assert.Equal("candidate_generated", tclXslDiag.Status);
        }
    }
}
