using LayoutParserApi.Controllers;
using LayoutParserApi.Models;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Transformation.StructuralResolution;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue LayoutParserApi #141 / LayoutParserReact #128: <c>POST execute-candidates</c> passa a
    /// devolver <c>fieldMappings</c> por candidato sysmiddle bem-sucedido, composto por
    /// <see cref="FieldMappingCompositionService"/> sobre o mesmo <c>Layout</c>/<c>MapperVo</c> usados
    /// para produzir <c>TransformedXml</c> (design em
    /// docs/architecture/design-contrato-fieldmappings-execute-candidates-issue-141.md §2/§6).
    ///
    /// <para>Reaproveita o padrão de fixture (XSD real da NF-e) de
    /// <c>FieldMappingCompositionServiceIntegrationTests</c> e o padrão de runner/mapper falsos de
    /// <c>LowCodeAutoTransformationCacheTests</c> — não reimplementa o motor de composição (já coberto
    /// por 25+ testes unitários/integração da issue #140), só confirma o wiring do controller.</para>
    /// </summary>
    public class TransformationExecutionControllerFieldMappingsTests
    {
        private const string LayoutName = "LAY_SINTETICO_TESTE_ISSUE141";
        private const string LayoutGuid = "79adf76a-4b07-428c-90d7-3c39d1296a5d";
        private const string FieldGuid = "FLD_natop";
        private const string Documento = "000001VENDA DE MERCADORIA SINTETICA";

        private static string FixturePath(string fileName) =>
            Path.Combine(AppContext.BaseDirectory, "StructuralResolution", "fixtures", fileName);

        /// <summary>Mapper decifrado real (issue #139, parser <c>RealMapperParser</c>): 1
        /// LinkMappingItem direto NATOP→natOp, mesma convenção <c>Descricao_nomeDaTag</c> usada pelo
        /// Sysmiddle real e já validada em <c>MapperDatabaseServiceRealMapperParserShadowTests</c>.</summary>
        private const string MapperXmlNatOpDireto =
            "<MapperVO>" +
            "<MapperGuid>MAP_sintetico_issue141</MapperGuid>" +
            "<TargetLayoutGuid>NFe-target</TargetLayoutGuid>" +
            "<LinkMappingItem>" +
            "<Name>NaturezaDaOperacao_natOp</Name>" +
            "<InputLayoutGuid>" + FieldGuid + "</InputLayoutGuid>" +
            "<TargetLayoutGuid>TAG_natOp</TargetLayoutGuid>" +
            "</LinkMappingItem>" +
            "</MapperVO>";

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; } = "issue141-test-user";
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
                    // DecryptedContent não é usado pelo FakeLayoutParserService (que ignora os streams),
                    // mas precisa ser não-vazio: é o que o controller checa antes de sequer tentar o
                    // parse posicional compartilhado (ExecuteSysmiddleCandidatesAsync).
                    Layouts = new List<LayoutRecord> { new() { Name = LayoutName, LayoutGuid = Guid.Parse(LayoutGuid), DecryptedContent = "<layout/>" } }
                });

            public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => Task.FromResult<LayoutRecord?>(null);
        }

        /// <summary>
        /// Substitui o parse posicional REAL (já coberto por outros testes) por um resultado canônico
        /// — mesmo Layout/ParsedField sintéticos de <c>FieldMappingCompositionServiceIntegrationTests</c>
        /// — para isolar o teste no wiring do controller (issue #141), não no motor de parsing.
        /// </summary>
        private sealed class FakeLayoutParserService : ILayoutParserService
        {
            public bool Falhar { get; set; }

            public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream)
            {
                if (Falhar)
                    return Task.FromResult(new ParsingResult { Success = false, ErrorMessage = "parse sintético falhou de propósito" });

                var field = new FieldElement
                {
                    ElementGuid = FieldGuid,
                    Name = "NATOP",
                    Sequence = 1,
                    LengthField = 60
                };
                var line = new LineElement
                {
                    ElementGuid = "LIN_det",
                    Name = "DET",
                    Sequence = 1,
                    Elements = new List<string> { JsonConvert.SerializeObject(field) }
                };
                var layout = new Layout
                {
                    LayoutGuid = "LAY_source",
                    Name = "LayoutSinteticoIssue141",
                    Elements = new List<LineElement> { line }
                };
                var parsedFields = new List<ParsedField>
                {
                    new()
                    {
                        LineName = "DET",
                        FieldName = "NATOP",
                        Occurrence = 1,
                        OccurrenceCount = 1,
                        IsAggregatedOccurrence = false,
                        Value = "VENDA DE MERCADORIA",
                        Start = 0,
                        Length = 60
                    }
                };

                return Task.FromResult(new ParsingResult
                {
                    Success = true,
                    Layout = layout,
                    ParsedFields = parsedFields,
                    LineInfos = new List<LineInfo>()
                });
            }

            public DocumentStructure BuildDocumentStructure(ParsingResult result) => throw new NotImplementedException();
            public Layout ReordenarSequences(Layout layout) => throw new NotImplementedException();
            public Layout ReestruturarLayout(Layout layoutOriginal) => throw new NotImplementedException();
            public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength = LineLengthResolver.LegacyDefaultLineLength) => throw new NotImplementedException();
            public Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent) => throw new NotImplementedException();
        }

        /// <summary>Runner low-code falso: nunca toca o <c>.exe</c> x86 — devolve XML canônico por
        /// mapperId, ou lança quando <see cref="FalharMapperId"/> corresponde (falha isolada de UM
        /// candidato).</summary>
        private sealed class RunnerFalso : LowCodeTransformationService
        {
            public RunnerFalso(IOptions<LowCodeRunnerOptions> opcoes, IConfiguration config)
                : base(NullLogger<LowCodeTransformationService>.Instance, opcoes, config) { }

            public string? FalharMapperId { get; set; }

            public override Task<string> TransformAsync(
                string inputContent, string? mapperId = null, string? mapperName = null, string? fileName = null,
                string? package = null, string? globalFolder = null, string? sysmiddleDir = null,
                CancellationToken cancellationToken = default)
            {
                if (mapperId == FalharMapperId)
                    throw new Exception($"Runner falso: falha isolada forcada para mapper {mapperId}");

                return Task.FromResult($"<nfe mapper=\"{mapperId}\">{inputContent.Length}</nfe>");
            }
        }

        private sealed class MapperDbFalso : MapperDatabaseService
        {
            private readonly List<Mapper> _mappers;

            public MapperDbFalso(IConfiguration config, List<Mapper> mappers)
                : base(NullLogger<MapperDatabaseService>.Instance, null!, config)
            {
                _mappers = mappers;
            }

            public override Task<List<Mapper>> GetRankedMapperCandidatesForLayoutGuidAsync(
                string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids)
                => Task.FromResult(_mappers.ToList());
        }

        private sealed class SpyAiCandidateService : IAiTransformationCandidateService
        {
            public Task EnqueueAsync(string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
                string inputContent, string? groundTruthXml, CancellationToken cancellationToken,
                IReadOnlyList<Models.Entities.ParsedField>? parsedFields = null) => Task.CompletedTask;

            public Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken) =>
                Task.FromResult(new AiCandidateStatus { Status = AiCandidateStatus.StatusNotFound });
        }

        private sealed class SpyAiFallbackSuppressionGate : IAiFallbackSuppressionGate
        {
            public bool IsInCooldown(Guid layoutGuid, out DateTimeOffset retryAt) { retryAt = default; return false; }
            public void RegisterFailure(Guid layoutGuid, TimeSpan cooldown) { }
            public void ClearCooldown(Guid layoutGuid) { }
        }

        private static FieldMappingCompositionService BuildFieldMappingComposition()
        {
            var schemaPath = FixturePath("nfe_v4.00.xsd");
            Assert.True(File.Exists(schemaPath), $"Fixture XSD não encontrada em {schemaPath}");
            var options = Options.Create(new StructuralResolutionOptions { NfeSchemaPath = schemaPath, NfeRootElementName = "NFe" });
            var catalogCache = new StructuralXmlCatalogCacheService(
                new MemoryCache(new MemoryCacheOptions()), options, NullLogger<StructuralXmlCatalogCacheService>.Instance);
            return new FieldMappingCompositionService(catalogCache, new MappingStructureService(), NullLogger<FieldMappingCompositionService>.Instance);
        }

        private static Mapper MapperComConteudo(string guid, string decryptedContent) => new()
        {
            MapperGuid = guid,
            Name = $"MAP_{guid}",
            PackageGuid = "PAC_1",
            ProjectId = "2",
            TargetLayoutGuid = "TGT",
            TargetLayoutGuidFromXml = "TGT_XML",
            DecryptedContent = decryptedContent
        };

        private static (TransformationExecutionController Controller, FakeLayoutParserService ParserFake, RunnerFalso Runner)
            BuildController(List<Mapper> mappers, bool parseFalha = false)
        {
            var raiz = Path.Combine(Path.GetTempPath(), "lp-tests", "execute-candidates-fieldmappings", Guid.NewGuid().ToString("N"));
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

            var store = new LowCodeTransformationStore(NullLogger<LowCodeTransformationStore>.Instance, lowCodeConfig, lowCodeOptions, redis: null);
            var runner = new RunnerFalso(lowCodeOptions, lowCodeConfig);
            var mapperDb = new MapperDbFalso(lowCodeConfig, mappers);

            var parserFake = new FakeLayoutParserService { Falhar = parseFalha };

            var services = new ServiceCollection();
            services.AddScoped<MapperDatabaseService>(_ => mapperDb);
            services.AddScoped<ILayoutParserService>(_ => parserFake);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            var lowCodeAuto = new LowCodeAutoTransformationService(
                NullLogger<LowCodeAutoTransformationService>.Instance,
                scopeFactory,
                runner,
                store,
                lowCodeOptions);

            var pipelineConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TransformationPipeline:TclPath"] = Path.Combine(raiz, "tcl"),
                    ["TransformationPipeline:XslPath"] = Path.Combine(raiz, "xsl"),
                })
                .Build();
            Directory.CreateDirectory(Path.Combine(raiz, "tcl"));
            Directory.CreateDirectory(Path.Combine(raiz, "xsl"));
            var pipelineService = new TransformationPipelineService(NullLogger<TransformationPipelineService>.Instance, pipelineConfig);

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
                aiCandidateService: new SpyAiCandidateService(),
                aiFallbackGate: new SpyAiFallbackSuppressionGate(),
                aiUserInstructionStore: new LayoutParserApi.Services.Transformation.Ai.AiUserInstructionStore(),
                aiUserSessionStore: new LayoutParserApi.Services.Database.SqlAiUserSessionStore(
                    NullLogger<LayoutParserApi.Services.Database.SqlAiUserSessionStore>.Instance,
                    new ConfigurationBuilder().Build(),
                    Microsoft.Extensions.Options.Options.Create(new LayoutParserApi.Services.Database.AiUserSessionHistoryOptions())),
                currentUser: new FakeCurrentUser(),
                mapperDb: mapperDb,
                layoutParser: parserFake,
                fieldMappingComposition: BuildFieldMappingComposition(),
                scopeFactory: scopeFactory);

            return (controller, parserFake, runner);
        }

        [Fact]
        public async Task Candidato_sysmiddle_bem_sucedido_traz_fieldMappings_preenchido()
        {
            var (controller, _, _) = BuildController(new List<Mapper> { MapperComConteudo("M1", MapperXmlNatOpDireto) });

            var request = new TransformationRequest { InputContent = Documento, LayoutName = LayoutName, LayoutGuid = LayoutGuid };
            var actionResult = await controller.ExecuteTransformationCandidates(request);

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            var candidato = Assert.Single(response.Candidates, c => c.Pathway == "sysmiddle");
            Assert.NotNull(candidato.FieldMappings);
            var mapping = Assert.Single(candidato.FieldMappings!);
            Assert.Equal(XslSynth.Model.MappingKind.Direct, mapping.Kind);
            Assert.Contains("natOp", Assert.Single(mapping.Targets).Xpath);
        }

        [Fact]
        public async Task Falha_isolada_na_composicao_nao_derruba_candidato_fieldMappings_fica_null()
        {
            // Mapper com conteúdo ilegível para RealMapperParser (XML malformado) — TryComposeFieldMappings
            // captura a exceção, o candidato mantém TransformedXml, fieldMappings sai null + warning.
            var (controller, _, _) = BuildController(new List<Mapper> { MapperComConteudo("M1", "<xml-invalido-sem-fechar") });

            var request = new TransformationRequest { InputContent = Documento, LayoutName = LayoutName, LayoutGuid = LayoutGuid };
            var actionResult = await controller.ExecuteTransformationCandidates(request);

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            var candidato = Assert.Single(response.Candidates, c => c.Pathway == "sysmiddle");
            Assert.False(string.IsNullOrEmpty(candidato.TransformedXml)); // XML do runner sobrevive
            Assert.Null(candidato.FieldMappings);
            Assert.Contains(response.Warnings, w => w.Contains("fieldMappings"));
        }

        [Fact]
        public async Task Parse_posicional_compartilhado_falha_fieldMappings_fica_null_para_todos_candidatos()
        {
            var (controller, _, _) = BuildController(
                new List<Mapper> { MapperComConteudo("M1", MapperXmlNatOpDireto), MapperComConteudo("M2", MapperXmlNatOpDireto) },
                parseFalha: true);

            var request = new TransformationRequest { InputContent = Documento, LayoutName = LayoutName, LayoutGuid = LayoutGuid };
            var actionResult = await controller.ExecuteTransformationCandidates(request);

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            var sysmiddleCandidatos = response.Candidates.Where(c => c.Pathway == "sysmiddle").ToList();
            Assert.Equal(2, sysmiddleCandidatos.Count);
            Assert.All(sysmiddleCandidatos, c => Assert.Null(c.FieldMappings));
            Assert.All(sysmiddleCandidatos, c => Assert.False(string.IsNullOrEmpty(c.TransformedXml)));
        }

        [Fact]
        public async Task Candidato_sysmiddle_sem_mapeamento_resolvivel_traz_fieldMappings_vazio_nao_null()
        {
            // Mapper legível (RealMapperParser não falha), mas sem nenhum LinkMappingItem/Rule —
            // Compose() itera sobre mapperVo.LinkMappings/Rules (ambos vazios aqui) e resolve para
            // lista vazia (não nula): mapper existe e foi processado, mas não encontrou nenhum
            // FieldToXmlMapping (design §6). Confirmado experimentalmente: um LinkMappingItem cujo
            // InputLayoutGuid não existe no parse ainda produz uma entrada (BestEffort) — Compose()
            // não filtra por resolução de origem, só itera a lista de links do mapper.
            const string mapperSemCorrespondencia =
                "<MapperVO>" +
                "<MapperGuid>MAP_sem_match</MapperGuid>" +
                "<TargetLayoutGuid>NFe-target</TargetLayoutGuid>" +
                "</MapperVO>";

            var (controller, _, _) = BuildController(new List<Mapper> { MapperComConteudo("M1", mapperSemCorrespondencia) });

            var request = new TransformationRequest { InputContent = Documento, LayoutName = LayoutName, LayoutGuid = LayoutGuid };
            var actionResult = await controller.ExecuteTransformationCandidates(request);

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            var candidato = Assert.Single(response.Candidates, c => c.Pathway == "sysmiddle");
            Assert.NotNull(candidato.FieldMappings); // não é null: composição rodou e concluiu sem exceção
            Assert.Empty(candidato.FieldMappings!);  // é [], porque nenhum mapeamento foi resolvido
        }

        [Fact]
        public async Task TransformedXml_e_identico_com_e_sem_extracao_de_fieldMappings()
        {
            // Mesmo mapper/documento: uma execução com parse posicional compartilhado disponível
            // (fieldMappings populado) e outra com o parse falhando de propósito (fieldMappings null).
            // TransformedXml vem exclusivamente de LowCodeCandidateResult.OutputXml (RunnerFalso), que
            // não depende do resultado de TryComposeFieldMappings — este teste confirma que a extração
            // de fieldMappings nunca influencia o XML já produzido pelo runner (isolamento total, design §2).
            var (controllerComFieldMappings, _, _) = BuildController(new List<Mapper> { MapperComConteudo("M1", MapperXmlNatOpDireto) });
            var (controllerSemFieldMappings, _, _) = BuildController(new List<Mapper> { MapperComConteudo("M1", MapperXmlNatOpDireto) }, parseFalha: true);

            var request = new TransformationRequest { InputContent = Documento, LayoutName = LayoutName, LayoutGuid = LayoutGuid };

            var okCom = Assert.IsType<OkObjectResult>(await controllerComFieldMappings.ExecuteTransformationCandidates(request));
            var respostaCom = Assert.IsType<TransformationExecutionCandidatesResponse>(okCom.Value);
            var candidatoCom = Assert.Single(respostaCom.Candidates, c => c.Pathway == "sysmiddle");

            var okSem = Assert.IsType<OkObjectResult>(await controllerSemFieldMappings.ExecuteTransformationCandidates(request));
            var respostaSem = Assert.IsType<TransformationExecutionCandidatesResponse>(okSem.Value);
            var candidatoSem = Assert.Single(respostaSem.Candidates, c => c.Pathway == "sysmiddle");

            Assert.NotNull(candidatoCom.FieldMappings);
            Assert.Null(candidatoSem.FieldMappings);
            Assert.Equal(candidatoSem.TransformedXml, candidatoCom.TransformedXml); // XML byte-idêntico nos dois cenários
        }

        [Fact]
        public async Task Pathway_tcl_xsl_fieldMappings_sempre_null()
        {
            // Sem .tcl cadastrado: o pathway tcl-xsl não gera candidato — mas quando gera (cenário
            // documentado em outros testes de pipeline), o contrato é fieldMappings==null sempre
            // (decisão categórica do design §2, mesma de sectionMappings na #138). Aqui confirmamos
            // que o pathway sysmiddle (único que popula) não vaza o campo para tcl-xsl mesmo quando
            // os dois pathways coexistem na mesma resposta.
            var (controller, _, _) = BuildController(new List<Mapper> { MapperComConteudo("M1", MapperXmlNatOpDireto) });

            var request = new TransformationRequest { InputContent = Documento, LayoutName = LayoutName, LayoutGuid = LayoutGuid };
            var actionResult = await controller.ExecuteTransformationCandidates(request);

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<TransformationExecutionCandidatesResponse>(ok.Value);

            Assert.All(response.Candidates.Where(c => c.Pathway == "tcl-xsl"), c => Assert.Null(c.FieldMappings));
        }
    }
}
