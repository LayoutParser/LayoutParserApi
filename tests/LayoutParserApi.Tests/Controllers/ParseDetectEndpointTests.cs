using System.Text;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Implementations;
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
    /// <c>POST /api/parse/detect</c> (issue #216) — detecção isolada de tipo, sem disparar parse
    /// completo. Usa o <see cref="LayoutDetector"/> real (não fake) porque o objetivo aqui é
    /// validar o contrato do endpoint em cima da heurística de verdade, não só a borda HTTP.
    /// </summary>
    public class ParseDetectEndpointTests
    {
        [Fact]
        public async Task Documento_mqseries_valido_responde_200_com_tipo_e_confianca_alta()
        {
            // Padrão mínimo aceito por LooksLikeMqSeries: começa com HEADER, tem >= 2 sequenciais
            // NNNNNNLLL e termina em linha 999 — replicando o cenário já coberto em
            // Parsing/PositionalFormatRegressionTests.cs para o detector.
            var conteudo = "HEADER" + new string('X', 600 - 6) + "000001000" + new string('Y', 600 - 9) + "000002999";

            var resposta = await ExecutarDetect(conteudo, "documento.mq_series");

            var ok = Assert.IsType<OkObjectResult>(resposta);
            Assert.Equal("mqseries", Ler<string>(ok.Value, "detectedType"));
            Assert.Equal("high", Ler<string>(ok.Value, "confidence"));
            Assert.Empty(Ler<System.Collections.IEnumerable>(ok.Value, "suggestedLayouts").Cast<object>());
        }

        [Fact]
        public async Task Documento_com_tipo_indetectavel_responde_200_com_txt_e_confianca_baixa()
        {
            // Conteúdo que não bate em nenhuma heurística (xml/mqseries/idoc) — LayoutDetector
            // devolve "unknown", que o endpoint normaliza para "txt" (mesmo fallback do upload,
            // ver GetLearningExtension em ParseController).
            var resposta = await ExecutarDetect("DADOS POSICIONAIS SEM PADRAO RECONHECIDO", "documento.txt");

            var ok = Assert.IsType<OkObjectResult>(resposta);
            Assert.Equal("txt", Ler<string>(ok.Value, "detectedType"));
            Assert.Equal("low", Ler<string>(ok.Value, "confidence"));
        }

        [Fact]
        public async Task Documento_xml_responde_200_com_tipo_xml()
        {
            var resposta = await ExecutarDetect("<?xml version=\"1.0\"?><NFe><infNFe/></NFe>", "documento.xml");

            var ok = Assert.IsType<OkObjectResult>(resposta);
            Assert.Equal("xml", Ler<string>(ok.Value, "detectedType"));
            Assert.Equal("high", Ler<string>(ok.Value, "confidence"));
        }

        [Fact]
        public async Task Arquivo_vazio_ainda_assim_responde_200_pois_deteccao_nao_e_o_gate_de_documento_vazio()
        {
            // O gate de "documento vazio" (422) vive só no fluxo de Upload — Detect é uma operação
            // de leitura isolada, então um arquivo vazio aqui só resulta em baixa confiança, não erro.
            var resposta = await ExecutarDetect("", "vazio.txt");

            var ok = Assert.IsType<OkObjectResult>(resposta);
            Assert.Equal("txt", Ler<string>(ok.Value, "detectedType"));
            Assert.Equal("low", Ler<string>(ok.Value, "confidence"));
        }

        [Fact]
        public async Task Sem_arquivo_responde_400()
        {
            var controller = CriarController();

            var resposta = await controller.Detect(null!, layoutName: null!);

            var badRequest = Assert.IsType<BadRequestObjectResult>(resposta);
            Assert.Equal(400, badRequest.StatusCode);
        }

        // ─────────────────────────────── infraestrutura do teste ───────────────────────────────

        private static async Task<IActionResult> ExecutarDetect(string conteudoDocumento, string nomeArquivo)
        {
            var controller = CriarController();
            return await controller.Detect(Arquivo(nomeArquivo, conteudoDocumento), layoutName: null!);
        }

        private static ParseController CriarController()
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
                new NoOpLayoutParserService(),
                NullLogger<ParseController>.Instance,
                new LayoutDetector(),
                new FileStorageService(config, NullLogger<FileStorageService>.Instance),
                new LayoutLearningService(NullLogger<LayoutLearningService>.Instance),
                config,
                lowCodeAuto,
                opcoesLowCode,
                store)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        private static T Ler<T>(object? payload, string propriedade)
        {
            Assert.NotNull(payload);

            var info = payload!.GetType().GetProperty(propriedade);
            Assert.True(info is not null, $"O payload não expõe a propriedade '{propriedade}'.");

            return (T)info!.GetValue(payload)!;
        }

        private static IFormFile Arquivo(string nome, string conteudo)
        {
            var bytes = Encoding.UTF8.GetBytes(conteudo);
            return new FormFile(new MemoryStream(bytes), 0, bytes.Length, nome, nome);
        }

        /// <summary>Detect nunca chama o parser — este fake só satisfaz o construtor.</summary>
        private sealed class NoOpLayoutParserService : ILayoutParserService
        {
            public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream) =>
                throw new InvalidOperationException("Detect não deveria chamar ParseAsync.");

            public Layout ReestruturarLayout(Layout layoutOriginal) => layoutOriginal;

            public Layout ReordenarSequences(Layout layout) => layout;

            public DocumentStructure BuildDocumentStructure(ParsingResult result) => new();

            public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength) => [];

            public Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent) => Task.FromResult<Layout?>(null);
        }
    }
}
