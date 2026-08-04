using System.Text;

using LayoutParserApi.Models.Enums;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Validation;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Parsing
{
    /// <summary>
    /// Fecha o circuito entre o parser e a taxonomia: <c>ParseAsync</c> captura a exceção
    /// internamente, e o TIPO dela só existe dentro daquele <c>catch</c> — daí pra frente sobra
    /// uma string. Se a classificação não acontecer ali, o controller fica sem como distinguir
    /// "arquivo ruim" de "bug nosso", e a taxonomia inteira vira decoração.
    /// </summary>
    public class ParseAsyncFailureCauseTests
    {
        /// <summary>
        /// Declaração XML obrigatória nas amostras. <c>LoadLayoutAsync</c> descarta o primeiro
        /// token <c>&lt;...&gt;</c> do arquivo assumindo que é a declaração — um layout sem
        /// <c>&lt;?xml?&gt;</c> perde o elemento RAIZ silenciosamente. Todo layout real tem a
        /// declaração, então a amostra também precisa ter, sob pena de o teste medir outra coisa.
        /// </summary>
        private const string DeclaracaoXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";

        [Fact]
        public async Task Layout_xml_malformado_classifica_como_layout_invalid()
        {
            var resultado = await ParseAsync(
                layoutXml: DeclaracaoXml + "<LayoutVO><Name>LAY_QUEBRADO</Name>",   // sem fechar a raiz
                documento: "000001CONTEUDO");

            Assert.False(resultado.Success);
            Assert.Equal(ParseFailureCause.LayoutInvalid, resultado.FailureCause);
        }

        [Fact]
        public async Task Parse_bem_sucedido_nao_registra_causa_de_falha()
        {
            var resultado = await ParseAsync(
                layoutXml: DeclaracaoXml + "<LayoutVO><Name>LAY_TESTE</Name><LayoutType>TextPositional</LayoutType></LayoutVO>",
                documento: "000001CONTEUDO");

            Assert.True(resultado.Success, resultado.ErrorMessage);
            Assert.Null(resultado.FailureCause);
            Assert.Equal("LAY_TESTE", resultado.Layout.Name);
        }

        /// <summary>
        /// LIMITE CONHECIDO, deliberadamente não atravessado nesta entrega: um XML bem-formado que
        /// NÃO é um layout (o usuário subiu o arquivo errado) não lança exceção — vira um Layout
        /// sem elementos e o parse "sucede" com zero campos.
        ///
        /// <para>Este é exatamente o caso para o qual o código de wire <c>layout_mismatch</c> ficou
        /// RESERVADO (spec §2.2): uma RELAÇÃO entre dois artefatos, não a invalidez de um
        /// (<see cref="ParseFailureCause.LayoutInvalid"/>). Transformá-lo em 422 cria uma falha
        /// nova onde hoje há um 200 — decisão de produto, não de implementação. Este teste trava o
        /// comportamento atual para que a mudança, quando vier, seja consciente.</para>
        /// </summary>
        [Fact]
        public async Task Xml_bem_formado_que_nao_e_layout_ainda_nao_e_classificado_como_mismatch()
        {
            var resultado = await ParseAsync(
                layoutXml: DeclaracaoXml + "<nfeProc><NFe><infNFe Id=\"NFe35\" /></NFe></nfeProc>",
                documento: "000001CONTEUDO");

            Assert.True(resultado.Success);
            Assert.Null(resultado.FailureCause);
            Assert.Empty(resultado.ParsedFields);
        }

        // ─────────────────────────────── infraestrutura do teste ───────────────────────────────

        private static async Task<LayoutParserApi.Models.Parsing.ParsingResult> ParseAsync(string layoutXml, string documento)
        {
            var techLogger = new NoOpTechLogger();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Mantém o aprendizado ML (fire-and-forget do ParseAsync) fora do repo.
                    ["ML:LearningDataPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "DocumentPatterns"),
                    ["ML:TrainingSamplesPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "TrainingSamples")
                })
                .Build();

            var service = new LayoutParserService(
                techLogger,
                new NoOpAuditLogger(),
                new LineSplitter(techLogger),
                new LayoutValidator(techLogger),
                new LayoutNormalizer(),
                new DocumentValidationService(techLogger, NullLogger<DocumentValidationService>.Instance),
                new DocumentMLValidationService(techLogger, NullLogger<DocumentMLValidationService>.Instance, config),
                NullLogger<LayoutParserService>.Instance);

            using var layoutStream = new MemoryStream(Encoding.UTF8.GetBytes(layoutXml));
            using var txtStream = new MemoryStream(Encoding.UTF8.GetBytes(documento));

            return await service.ParseAsync(layoutStream, txtStream);
        }

        private sealed class NoOpTechLogger : ITechLogger
        {
            public void LogTechnical(LogEntry entry) { }
        }

        private sealed class NoOpAuditLogger : IAuditLogger
        {
            public void LogAudit(AuditLogEntry entry) { }
        }
    }
}
