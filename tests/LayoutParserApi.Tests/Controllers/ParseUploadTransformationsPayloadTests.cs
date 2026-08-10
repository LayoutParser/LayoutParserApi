using System.Text;
using System.Text.Json;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// O que o <c>POST /api/parse/upload</c> passa a devolver sobre as transformações
    /// (<c>spec-entrega-da-transformacao-no-parse.md</c> §2.6) — <b>aditivo</b>: nada foi removido e
    /// <c>transformationsStatus</c> mantém os mesmos quatro valores.
    ///
    /// <para>O caso que fecha o ciclo: XML acima do teto inline sai <b>omitido</b> do payload, e o
    /// front busca o corpo pelo ticket. Aqui isso é exercitado de ponta a ponta — upload, manifesto
    /// e corpo — porque é exatamente onde um contrato meia-boca quebraria o front.</para>
    /// </summary>
    public class ParseUploadTransformationsPayloadTests
    {
        private const string LayoutGuid = "79adf76a-4b07-428c-90d7-3c39d1296a5d";
        private const string Documento = "000001DADOS DO DOCUMENTO POSICIONAL";

        [Fact]
        public async Task Xml_pequeno_vai_inline_com_ticket_e_outputLength()
        {
            var (controller, _, _) = CriarController(xmlDoRunner: "<nfe>ok</nfe>");

            var ok = Assert.IsType<OkObjectResult>(await ExecutarUpload(controller));

            Assert.Equal("completed", Ler<string>(ok.Value, "transformationsStatus"));

            var ticket = Ler<string>(ok.Value, "transformationsTicket");
            Assert.Equal(LowCodeTransformationStore.BuildTicketFromContent(Documento, LayoutGuid), ticket);

            var candidato = Assert.Single(Assert.IsType<List<LowCodeCandidateResult>>(Ler<object>(ok.Value, "transformations")));
            Assert.Equal("<nfe>ok</nfe>", candidato.OutputXml);
            Assert.Equal("<nfe>ok</nfe>".Length, candidato.OutputLength);
        }

        /// <summary>
        /// Acima do teto: <c>outputXml</c> é omitido (o serializador ignora nulos) mas
        /// <c>outputLength</c> vai — sem ele, "campo ausente" seria indistinguível de "candidato sem
        /// saída". E o corpo continua alcançável pelo ticket.
        /// </summary>
        [Fact]
        public async Task Xml_acima_do_teto_e_omitido_e_buscavel_pelo_ticket()
        {
            var xmlGrande = "<nfe>" + new string('x', 500) + "</nfe>";
            var (controller, _, _) = CriarController(xmlDoRunner: xmlGrande, tetoInline: 100);

            var ok = Assert.IsType<OkObjectResult>(await ExecutarUpload(controller));

            var candidato = Assert.Single(Assert.IsType<List<LowCodeCandidateResult>>(Ler<object>(ok.Value, "transformations")));
            Assert.Null(candidato.OutputXml);
            Assert.Equal(xmlGrande.Length, candidato.OutputLength);

            // A ramificação do front: candidate.outputXml ?? fetchBody(ticket, mapperGuid).
            var ticket = Ler<string>(ok.Value, "transformationsTicket");
            var corpo = Assert.IsType<OkObjectResult>(await controller.GetTransformationCandidate(ticket, "M1"));

            Assert.Equal(xmlGrande, Ler<string>(corpo.Value, "outputXml"));
        }

        [Fact]
        public async Task Ticket_do_upload_resolve_no_manifesto()
        {
            var (controller, _, _) = CriarController(xmlDoRunner: "<nfe>ok</nfe>");

            var ok = Assert.IsType<OkObjectResult>(await ExecutarUpload(controller));
            var ticket = Ler<string>(ok.Value, "transformationsTicket");

            var manifesto = Assert.IsType<OkObjectResult>(await controller.GetTransformations(ticket));

            Assert.Equal("completed", Ler<string>(manifesto.Value, "status"));
        }

        /// <summary>
        /// O caso que motivou o ticket: a entrega síncrona não deu tempo. Antes o front recebia
        /// <c>processing</c> e não tinha a quem perguntar — o rótulo "(processando...)" ficava preso
        /// para sempre. Agora o ticket sai <b>junto</b> com o status.
        /// </summary>
        [Fact]
        public async Task Estouro_do_teto_sincrono_ainda_devolve_o_ticket()
        {
            var (controller, runner, store) = CriarController(xmlDoRunner: "<nfe>ok</nfe>", tetoSincronoSegundos: 1);
            runner.Travar = true;

            try
            {
                var ok = Assert.IsType<OkObjectResult>(await ExecutarUpload(controller));

                Assert.Equal("processing", Ler<string>(ok.Value, "transformationsStatus"));

                var ticket = Ler<string>(ok.Value, "transformationsTicket");
                Assert.False(string.IsNullOrWhiteSpace(ticket));

                // E o ticket já resolve — em "processing", que é a resposta honesta neste instante.
                var manifesto = Assert.IsType<OkObjectResult>(await controller.GetTransformations(ticket));
                Assert.Equal("processing", Ler<string>(manifesto.Value, "status"));
            }
            finally
            {
                runner.Liberar();
            }
        }

        [Fact]
        public async Task Payload_do_upload_nao_carrega_caminho_absoluto_do_servidor()
        {
            var (controller, runner, _) = CriarController(xmlDoRunner: "<nfe>ok</nfe>");
            runner.Falhar = true;

            var ok = Assert.IsType<OkObjectResult>(await ExecutarUpload(controller));
            var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            Assert.DoesNotContain(@"C:\", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("inetpub", json, StringComparison.OrdinalIgnoreCase);
        }

        // ─────────────────────────────── infraestrutura ───────────────────────────────

        private static async Task<IActionResult> ExecutarUpload(ParseController controller)
            => await controller.Upload(
                Arquivo("layout.xml", "<LayoutVO><Name>LAY_TESTE</Name></LayoutVO>"),
                Arquivo("documento.mq_series.txt", Documento),
                layoutName: null!);

        private static (ParseController controller, RunnerFalso runner, LowCodeTransformationStore store) CriarController(
            string xmlDoRunner,
            int tetoInline = 262144,
            int tetoSincronoSegundos = 6)
        {
            var raiz = Path.Combine(Path.GetTempPath(), "lp-tests", "lowcode-payload", Guid.NewGuid().ToString("N"));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LowCodeTransformationsPath"] = raiz,
                    ["Logging:File:Directory"] = Path.Combine(Path.GetTempPath(), "lp-tests", "runner-logs"),
                    ["TransformationPipeline:ExamplesPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "Examples")
                })
                .Build();

            var opcoes = Options.Create(new LowCodeRunnerOptions
            {
                RunnerPath = "runner-inexistente.exe",
                SysmiddleDir = Path.GetTempPath(),
                GlobalFolder = Path.GetTempPath(),
                InlineXmlMaxChars = tetoInline,
                SyncDeliveryTimeoutSeconds = tetoSincronoSegundos
            });

            var store = new LowCodeTransformationStore(
                NullLogger<LowCodeTransformationStore>.Instance, config, opcoes, redis: null);

            var runner = new RunnerFalso(opcoes, config, xmlDoRunner);

            var servicos = new ServiceCollection();
            servicos.AddScoped<MapperDatabaseService>(_ => new MapperDbFalso(config));

            var lowCodeAuto = new LowCodeAutoTransformationService(
                NullLogger<LowCodeAutoTransformationService>.Instance,
                servicos.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                runner,
                store,
                opcoes);

            var controller = new ParseController(
                new FakeLayoutParserService(),
                NullLogger<ParseController>.Instance,
                new FakeLayoutDetector(),
                new FileStorageService(config, NullLogger<FileStorageService>.Instance),
                new LayoutLearningService(NullLogger<LayoutLearningService>.Instance),
                config,
                lowCodeAuto,
                opcoes,
                store)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            return (controller, runner, store);
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

        private sealed class FakeLayoutDetector : ILayoutDetector
        {
            public string DetectType(string content) => "mqseries";
        }

        private sealed class FakeLayoutParserService : ILayoutParserService
        {
            public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream) => Task.FromResult(new ParsingResult
            {
                Success = true,
                Layout = new Layout { LayoutGuid = LayoutGuid, Name = "LAY_TESTE", LayoutType = "TextPositional" },
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

        private sealed class RunnerFalso : LowCodeTransformationService
        {
            private readonly string _xml;
            private readonly TaskCompletionSource _travado = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public RunnerFalso(IOptions<LowCodeRunnerOptions> opcoes, IConfiguration config, string xml)
                : base(NullLogger<LowCodeTransformationService>.Instance, opcoes, config)
            {
                _xml = xml;
            }

            /// <summary>Segura a transformação até <see cref="Liberar"/> — força o estouro do teto síncrono.</summary>
            public bool Travar { get; set; }

            public bool Falhar { get; set; }

            public void Liberar() => _travado.TrySetResult();

            public override async Task<string> TransformAsync(
                string inputContent,
                string? mapperId = null,
                string? mapperName = null,
                string? fileName = null,
                string? package = null,
                string? globalFolder = null,
                string? sysmiddleDir = null,
                CancellationToken cancellationToken = default)
            {
                if (Travar)
                {
                    try
                    {
                        await _travado.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // O runner real não morre no instante do cancelamento: há a janela de kill
                        // do processo (até 2s). Modelamos isso para o teste não depender de quem
                        // ganha uma corrida de microssegundos entre fechar o índice e consultá-lo.
                        await Task.Delay(300, CancellationToken.None);
                        throw;
                    }
                }

                if (Falhar)
                    throw new Exception($@"Runner nao gerou outputFile: C:\inetpub\wwwroot\layoutparser\api\out_{mapperId}.xml");

                return _xml;
            }
        }

        private sealed class MapperDbFalso : MapperDatabaseService
        {
            public MapperDbFalso(IConfiguration config)
                : base(NullLogger<MapperDatabaseService>.Instance, null!, config)
            {
            }

            public override Task<List<Mapper>> GetRankedMapperCandidatesForLayoutGuidAsync(
                string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids)
                => Task.FromResult(new List<Mapper>
                {
                    new()
                    {
                        MapperGuid = "M1",
                        Name = "MAP_TESTE",
                        PackageGuid = "PAC_1",
                        ProjectId = "2",
                        TargetLayoutGuid = "TGT",
                        TargetLayoutGuidFromXml = "TGT_XML"
                    }
                });
        }
    }
}
