using System.Text;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
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
    /// Semântica de status do <c>POST /api/parse/upload</c>
    /// (<c>docs/architecture/spec-taxonomia-de-falha-do-parse.md</c> §2).
    ///
    /// <para>Três realidades que antes eram dois desfechos: defeito localizável (200 anotado),
    /// entrada irrecuperável (422) e defeito nosso (500). Estes testes travam a fronteira entre
    /// elas na borda HTTP — é lá que a decisão vira comportamento observável pelo front.</para>
    /// </summary>
    public class ParseControllerTaxonomyTests
    {
        // ── 200: parseou, com ou sem defeito ────────────────────────────────────────────────────

        /// <summary>
        /// O caso que motivou a spec: documento parseável COM defeito. Ele não pode virar 422 —
        /// é entidade processável com problema, e o usuário precisa VER o documento com o erro
        /// anotado. Se isto virar 422 de novo, o front esconde um documento que existe.
        /// </summary>
        [Fact]
        public async Task Defeito_localizavel_responde_200_com_documento_e_erros_anotados()
        {
            var parse = ParseComSucesso();
            parse.ValidationErrors =
            [
                new DocumentValidationErrorInfo
                {
                    LineIndex = 37,
                    Sequence = "000037",
                    ExpectedLength = 600,
                    ActualLength = 578,
                    StartPosition = 100,
                    EndPosition = 140,
                    ErrorMessage = "Última linha tem 578 caracteres (esperado: 600)."
                }
            ];

            var resposta = await ExecutarUpload(parse);

            var ok = Assert.IsType<OkObjectResult>(resposta);
            Assert.Equal(200, ok.StatusCode);
            Assert.True(Ler<bool>(ok.Value, "success"));
            Assert.Equal("has_defects", Ler<string>(ok.Value, "documentHealth"));

            // O documento continua inteiro no payload — 200 "com defeito" não é 200 vazio.
            Assert.NotNull(Ler<object>(ok.Value, "layout"));
            Assert.NotNull(Ler<object>(ok.Value, "text"));

            var erros = Assert.IsType<List<DocumentValidationErrorInfo>>(Ler<object>(ok.Value, "validationErrors"));
            Assert.Equal(37, Assert.Single(erros).LineIndex);
        }

        [Fact]
        public async Task Parse_limpo_responde_200_com_documentHealth_clean()
        {
            var resposta = await ExecutarUpload(ParseComSucesso());

            var ok = Assert.IsType<OkObjectResult>(resposta);
            Assert.Equal("clean", Ler<string>(ok.Value, "documentHealth"));
        }

        /// <summary>
        /// Guarda de escopo: a taxonomia não podia mexer no pathway low-code. Layout sem GUID
        /// continua caindo em <c>not_applicable</c>, como antes.
        /// </summary>
        [Fact]
        public async Task Taxonomia_nao_mexe_no_status_das_transformacoes()
        {
            var resposta = await ExecutarUpload(ParseComSucesso());

            var ok = Assert.IsType<OkObjectResult>(resposta);
            Assert.Equal("not_applicable", Ler<string>(ok.Value, "transformationsStatus"));
        }

        // ── 500: defeito nosso ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Exceção não catalogada = bug nosso. Vira 500, não 422 — e a mensagem NÃO pode carregar
        /// o texto interno da exceção.
        /// </summary>
        [Fact]
        public async Task Defeito_do_parser_responde_500_com_mensagem_segura()
        {
            var parse = ParseQueFalhou(
                ParseFailureCause.ParserDefect,
                "Erro no parsing: Object reference not set to an instance of an object.");

            var resposta = await ExecutarUpload(parse);

            var erro = Assert.IsType<ObjectResult>(resposta);
            Assert.Equal(500, erro.StatusCode);
            Assert.False(Ler<bool>(erro.Value, "success"));
            Assert.Equal("parser_defect", Ler<string>(erro.Value, "failureCause"));

            var mensagem = Ler<string>(erro.Value, "message");
            Assert.Equal(ParseFailure.ParserDefectSafeMessage, mensagem);
            Assert.DoesNotContain("Object reference", mensagem, StringComparison.OrdinalIgnoreCase);

            Assert.False(string.IsNullOrWhiteSpace(Ler<string>(erro.Value, "correlationId")));
        }

        /// <summary>
        /// Falha sem exceção catalogada (<c>FailureCause</c> nulo) também é nossa. É o default da
        /// regra: na dúvida, a culpa é nossa — nunca do arquivo do usuário.
        /// </summary>
        [Fact]
        public async Task Falha_sem_causa_registrada_responde_500()
        {
            var parse = ParseQueFalhou(causa: null, motivo: "Não foi possível parsear.");

            var erro = Assert.IsType<ObjectResult>(await ExecutarUpload(parse));

            Assert.Equal(500, erro.StatusCode);
            Assert.Equal("parser_defect", Ler<string>(erro.Value, "failureCause"));
        }

        // ── 422: entrada irrecuperável ─────────────────────────────────────────────────────────

        /// <summary>
        /// Não regride o 422 de <c>7f54e28</c>: falha de entrada continua 422, agora com causa
        /// nomeada, e o motivo real segue visível (é a informação útil pro usuário).
        /// </summary>
        [Fact]
        public async Task Layout_ilegivel_responde_422_com_causa_e_motivo_real()
        {
            const string motivo = "Erro no parsing: 'x' é um caractere inesperado na linha 3.";
            var parse = ParseQueFalhou(ParseFailureCause.LayoutMismatch, motivo);

            var erro = Assert.IsType<ObjectResult>(await ExecutarUpload(parse));

            Assert.Equal(422, erro.StatusCode);
            Assert.Equal("layout_mismatch", Ler<string>(erro.Value, "failureCause"));
            Assert.Equal(motivo, Ler<string>(erro.Value, "message"));
            Assert.Equal("mqseries", Ler<string>(erro.Value, "detectedType"));
        }

        /// <summary>
        /// Documento vazio: sem este gate o parse "sucede" com zero campos e o payload sairia
        /// <c>documentHealth: "clean"</c> — documento vazio não é documento limpo.
        /// </summary>
        [Fact]
        public async Task Documento_vazio_responde_422_document_malformed()
        {
            var erro = Assert.IsType<ObjectResult>(
                await ExecutarUpload(ParseComSucesso(), conteudoDocumento: ""));

            Assert.Equal(422, erro.StatusCode);
            Assert.Equal("document_malformed", Ler<string>(erro.Value, "failureCause"));
            Assert.False(string.IsNullOrWhiteSpace(Ler<string>(erro.Value, "correlationId")));
        }

        // ─────────────────────────────── infraestrutura do teste ───────────────────────────────

        private const string DocumentoPadrao = "000001DADOS DO DOCUMENTO POSICIONAL";

        private static ParsingResult ParseComSucesso() => new()
        {
            Success = true,
            Layout = new Layout { Name = "LAY_TESTE", LayoutType = "TextPositional" },
            ParsedFields = [new ParsedField { LineName = "LINHA000", FieldName = "CUF", Value = "35" }],
            RawText = DocumentoPadrao,
            ValidationErrors = []
        };

        private static ParsingResult ParseQueFalhou(ParseFailureCause? causa, string motivo) => new()
        {
            Success = false,
            Layout = null,
            ErrorMessage = motivo,
            FailureCause = causa
        };

        private static async Task<IActionResult> ExecutarUpload(
            ParsingResult resultadoDoParse,
            string conteudoDocumento = DocumentoPadrao)
        {
            var controller = CriarController(resultadoDoParse);

            return await controller.Upload(
                Arquivo("layout.xml", "<LayoutVO><Name>LAY_TESTE</Name></LayoutVO>"),
                Arquivo("documento.mq_series.txt", conteudoDocumento),
                layoutName: null!);
        }

        private static ParseController CriarController(ParsingResult resultadoDoParse)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Mantém qualquer escrita de disco dos serviços satélites fora do repo.
                    ["TransformationPipeline:ExamplesPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "Examples"),
                    ["ML:LowCodeTransformationsPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "LowCode")
                })
                .Build();

            var opcoesLowCode = Options.Create(new LowCodeRunnerOptions());

            var lowCodeAuto = new LowCodeAutoTransformationService(
                NullLogger<LowCodeAutoTransformationService>.Instance,
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new LowCodeTransformationService(NullLogger<LowCodeTransformationService>.Instance, opcoesLowCode, config),
                config,
                opcoesLowCode);

            var controller = new ParseController(
                new FakeLayoutParserService(resultadoDoParse),
                NullLogger<ParseController>.Instance,
                new FakeLayoutDetector(),
                new FileStorageService(config, NullLogger<FileStorageService>.Instance),
                new LayoutLearningService(NullLogger<LayoutLearningService>.Instance),
                config,
                lowCodeAuto,
                opcoesLowCode)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            return controller;
        }

        /// <summary>Lê uma propriedade do objeto anônimo devolvido pelo controller.</summary>
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

        private sealed class FakeLayoutDetector : ILayoutDetector
        {
            public string DetectType(string content) => "mqseries";
        }

        /// <summary>
        /// Devolve um <see cref="ParsingResult"/> pré-fabricado: o alvo do teste é a DECISÃO da
        /// borda HTTP a partir do resultado, não o parser (coberto em <c>Parsing/</c>).
        /// </summary>
        private sealed class FakeLayoutParserService(ParsingResult resultado) : ILayoutParserService
        {
            public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream) => Task.FromResult(resultado);

            public Layout ReestruturarLayout(Layout layoutOriginal) => layoutOriginal;

            public Layout ReordenarSequences(Layout layout) => layout;

            public DocumentStructure BuildDocumentStructure(ParsingResult result) => new();

            public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength) => [];

            public Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent) => Task.FromResult<Layout?>(null);
        }
    }
}
