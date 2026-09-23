using System.Text;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Tests.TestHelpers;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #366 — registro de histórico DENTRO de <c>POST /api/parse/upload</c>: opt-in por
    /// <c>workspaceId</c>, e falha de persistência NUNCA derruba a análise (200 com
    /// <c>historyRegistered=false</c>, sem <c>analysisId</c>).
    /// </summary>
    public class ParseUploadAnalysisHistoryTests
    {
        private const string Documento = "000001DADOS DO DOCUMENTO POSICIONAL";
        private static readonly Guid Workspace = Guid.NewGuid();
        private static readonly Guid User = Guid.NewGuid();

        [Fact]
        public async Task Com_workspaceId_registra_documento_e_layout_e_devolve_analysisId()
        {
            var svc = new FakeAnalysisService { ResultId = Guid.NewGuid() };
            var controller = CriarController(svc, User);

            var ok = Assert.IsType<OkObjectResult>(await Executar(controller, Workspace.ToString()));

            Assert.True(Ler<bool>(ok.Value, "historyRegistered"));
            Assert.Equal(svc.ResultId, Ler<Guid?>(ok.Value, "analysisId"));
            var reg = Assert.IsType<FiscalAnalysisRegistration>(svc.LastRegistration);
            Assert.Equal(FiscalAnalysisSource.Upload, reg.Source);
            Assert.Equal(FiscalAnalysisLayoutMode.File, reg.LayoutMode);
            Assert.Equal(User, reg.OwnerUserId);
            Assert.Contains(reg.Files, f => f.Role == FiscalAnalysisFileRole.Document && Encoding.UTF8.GetString(f.Content) == Documento);
            Assert.Contains(reg.Files, f => f.Role == FiscalAnalysisFileRole.Layout);
        }

        [Fact]
        public async Task Falha_de_persistencia_nao_derruba_a_analise()
        {
            var svc = new FakeAnalysisService { Throw = true };
            var controller = CriarController(svc, User);

            var ok = Assert.IsType<OkObjectResult>(await Executar(controller, Workspace.ToString()));

            Assert.False(Ler<bool>(ok.Value, "historyRegistered"));
            Assert.Null(Ler<Guid?>(ok.Value, "analysisId"));
            Assert.True(Ler<bool>(ok.Value, "success")); // o parse segue íntegro
        }

        [Fact]
        public async Task Servico_devolvendo_null_vira_analysisId_nulo()
        {
            var controller = CriarController(new FakeAnalysisService { ResultId = null }, User);

            var ok = Assert.IsType<OkObjectResult>(await Executar(controller, Workspace.ToString()));

            Assert.False(Ler<bool>(ok.Value, "historyRegistered"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nao-e-guid")]
        public async Task Sem_workspaceId_valido_nao_registra(string? workspaceId)
        {
            var svc = new FakeAnalysisService { ResultId = Guid.NewGuid() };
            var controller = CriarController(svc, User);

            var ok = Assert.IsType<OkObjectResult>(await Executar(controller, workspaceId));

            Assert.False(Ler<bool>(ok.Value, "historyRegistered"));
            Assert.Equal(0, svc.Calls);
        }

        [Fact]
        public async Task Usuario_anonimo_nao_registra()
        {
            var svc = new FakeAnalysisService { ResultId = Guid.NewGuid() };
            var controller = CriarController(svc, userId: null);

            var ok = Assert.IsType<OkObjectResult>(await Executar(controller, Workspace.ToString()));

            Assert.False(Ler<bool>(ok.Value, "historyRegistered"));
            Assert.Equal(0, svc.Calls);
        }

        // ─────────────────────────────── infraestrutura ───────────────────────────────

        private static Task<IActionResult> Executar(ParseController controller, string? workspaceId)
            => controller.Upload(
                Arquivo("layout.xml", "<LayoutVO><Name>LAY_TESTE</Name></LayoutVO>"),
                Arquivo("documento.mq_series.txt", Documento),
                layoutName: null!,
                workspaceId: workspaceId);

        private static ParseController CriarController(IFiscalAnalysisService svc, Guid? userId)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TransformationPipeline:ExamplesPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "Examples"),
                    ["ML:LowCodeTransformationsPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "LowCode")
                })
                .Build();

            var opcoesLowCode = Options.Create(new LowCodeRunnerOptions());
            var store = new LowCodeTransformationStore(
                NullLogger<LowCodeTransformationStore>.Instance, config, opcoesLowCode, redis: null);
            var lowCodeAuto = new LowCodeAutoTransformationService(
                NullLogger<LowCodeAutoTransformationService>.Instance,
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new LowCodeTransformationService(NullLogger<LowCodeTransformationService>.Instance, opcoesLowCode, config),
                store,
                opcoesLowCode);

            return new ParseController(
                new FakeLayoutParserService(),
                NullLogger<ParseController>.Instance,
                new FakeLayoutDetector(),
                new FileStorageService(config, NullLogger<FileStorageService>.Instance),
                new LayoutLearningService(NullLogger<LayoutLearningService>.Instance, new LineSplitter(new NoOpTechLogger())),
                config,
                lowCodeAuto,
                opcoesLowCode,
                store,
                svc,
                new FakeUser { UserId = userId })
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        private static IFormFile Arquivo(string nome, string conteudo)
        {
            var bytes = Encoding.UTF8.GetBytes(conteudo);
            return new FormFile(new MemoryStream(bytes), 0, bytes.Length, nome, nome);
        }

        private static T Ler<T>(object? payload, string propriedade)
        {
            Assert.NotNull(payload);
            var info = payload!.GetType().GetProperty(propriedade);
            Assert.True(info is not null, $"O payload não expõe a propriedade '{propriedade}'.");
            return (T)info!.GetValue(payload)!;
        }

        private sealed class FakeUser : ICurrentUser
        {
            public string? Name => UserId.HasValue ? "u" : null;
            public IReadOnlyList<string> Roles => [];
            public bool IsAuthenticated => UserId.HasValue;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => false;
        }

        private sealed class FakeAnalysisService : IFiscalAnalysisService
        {
            public Guid? ResultId { get; set; }
            public bool Throw { get; set; }
            public int Calls { get; private set; }
            public FiscalAnalysisRegistration? LastRegistration { get; private set; }

            public Task<Guid?> RegisterAsync(FiscalAnalysisRegistration registration, TimeSpan timeout)
            {
                Calls++;
                LastRegistration = registration;
                if (Throw) throw new InvalidOperationException("SQL fora do ar");
                return Task.FromResult(ResultId);
            }

            public Task<FiscalAnalysisPage> ListAsync(Guid workspaceId, Guid ownerUserId, int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
            public Task<FiscalAnalysisDetail?> GetAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken ct) => throw new NotSupportedException();
            public Task<FiscalAnalysisFileContent> OpenFileAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, Guid fileId, CancellationToken ct) => throw new NotSupportedException();
            public Task<bool> DeleteAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken ct) => throw new NotSupportedException();
            public Task<int> PurgeExpiredAsync(CancellationToken ct) => throw new NotSupportedException();
        }

        private sealed class FakeLayoutDetector : ILayoutDetector
        {
            public string DetectType(string content) => "mqseries";
        }

        private sealed class FakeLayoutParserService : ILayoutParserService
        {
            public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream) => Task.FromResult(new ParsingResult
            {
                Success = true,
                Layout = new Layout { Name = "LAY_TESTE", LayoutType = "TextPositional" },
                ParsedFields = [new ParsedField { LineName = "LINHA000", FieldName = "CUF", Value = "35" }],
                RawText = Documento,
                ValidationErrors = []
            });

            public Layout ReestruturarLayout(Layout layoutOriginal) => layoutOriginal;
            public Layout ReordenarSequences(Layout layout) => layout;
            public DocumentStructure BuildDocumentStructure(ParsingResult result) => new();
            public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength) => [];
            public Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent) => Task.FromResult<Layout?>(null);
        }
    }
}
