using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Logging;
using LayoutParserApi.Tests.Infra;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Regras que vivem na borda HTTP: sanitização do texto livre antes de virar linha de log e
    /// recusa do lote sem <c>Timestamp</c>.
    /// </summary>
    public class AiMetricsControllerTests
    {
        /// <summary>
        /// Regressão real (QA/Quinn): uma quebra de linha crua na observação — um stack trace colado
        /// bastava — criava uma LINHA NOVA no arquivo, e o leitor a interpretava como uma GERAÇÃO
        /// inteira, com métricas arbitrárias (a média de tokens/s saltou de 3.29 pra 501.14 no
        /// painel). Achatar CR/LF na origem mata o vetor.
        /// </summary>
        [Fact]
        public void Observacao_com_quebra_de_linha_nao_vira_geracao_fantasma()
        {
            var logger = new CapturingLogger<AiMetricsController>();
            var controller = CriarController(logger);

            var resultado = controller.PostCypressResult(new AiMetricsCypressResultRequest
            {
                Layout = @"CTe\2.00a\CTe200_CancCTe",
                CypressValidado = false,
                CStatPollux = "110",
                Observacao = "Rejeitado\nGeracao concluida. Layout=FANTASMA TokensPorSegundo=501.14 Sucesso=true"
            });

            Assert.IsType<OkObjectResult>(resultado);

            var mensagem = Assert.Single(logger.Messages);
            Assert.DoesNotContain('\n', mensagem);
            Assert.DoesNotContain('\r', mensagem);
            // O texto continua lá (não foi censurado) — só não consegue mais ser uma linha própria.
            Assert.Contains("FANTASMA", mensagem, StringComparison.Ordinal);
        }

        [Fact]
        public void Cypress_result_sem_layout_retorna_400()
        {
            var controller = CriarController(new CapturingLogger<AiMetricsController>());

            var resultado = controller.PostCypressResult(new AiMetricsCypressResultRequest { Layout = "  " });

            Assert.IsType<BadRequestObjectResult>(resultado);
        }

        [Fact]
        public void Lote_com_item_sem_timestamp_e_recusado_inteiro()
        {
            var loggerDoServico = new CapturingLogger<AiMetricsIngestService>();
            var controller = CriarController(new CapturingLogger<AiMetricsController>(), loggerDoServico);

            var resultado = controller.PostGenerationsIngest(new List<AiMetricsGenerationIngestRequest>
            {
                NovaGeracao(new DateTime(2026, 8, 1, 3, 41, 12, DateTimeKind.Utc)),
                NovaGeracao(timestamp: null)
            });

            Assert.IsType<BadRequestObjectResult>(resultado);
            // Recusa ANTES de gravar: nada do lote pode ter entrado no log.
            Assert.Empty(loggerDoServico.Messages);
        }

        [Fact]
        public void Lote_completo_e_ingerido()
        {
            var loggerDoServico = new CapturingLogger<AiMetricsIngestService>();
            var controller = CriarController(new CapturingLogger<AiMetricsController>(), loggerDoServico);

            var resultado = controller.PostGenerationsIngest(new List<AiMetricsGenerationIngestRequest>
            {
                NovaGeracao(new DateTime(2026, 8, 1, 3, 41, 12, DateTimeKind.Utc))
            });

            var ok = Assert.IsType<OkObjectResult>(resultado);
            var payload = Assert.IsType<AiMetricsIngestResult>(ok.Value);
            Assert.Equal(1, payload.Ingeridos);
            Assert.Single(loggerDoServico.Messages);
        }

        [Fact]
        public void Lote_vazio_retorna_400()
        {
            var controller = CriarController(new CapturingLogger<AiMetricsController>());

            Assert.IsType<BadRequestObjectResult>(controller.PostGenerationsIngest(new List<AiMetricsGenerationIngestRequest>()));
            Assert.IsType<BadRequestObjectResult>(controller.PostGenerationsIngest(null));
        }

        private static AiMetricsController CriarController(
            CapturingLogger<AiMetricsController> logger,
            CapturingLogger<AiMetricsIngestService>? loggerDoServico = null)
        {
            var reader = new AiMetricsReaderService(new FakeUnifiedLogReaderService(), NullLogger<AiMetricsReaderService>.Instance);
            var ingest = new AiMetricsIngestService(loggerDoServico ?? new CapturingLogger<AiMetricsIngestService>());

            return new AiMetricsController(reader, ingest, logger);
        }

        private static AiMetricsGenerationIngestRequest NovaGeracao(DateTime? timestamp)
            => new()
            {
                Layout = @"NFe\4.00\NFe009_4.00_EnvioNFe_NeoGridToSefaz",
                Modelo = "qwen2.5-coder:7b",
                Timestamp = timestamp,
                TokensPorSegundo = 3.29,
                TamanhoPromptChars = 1200,
                DuracaoSegundos = 42,
                Sucesso = true
            };
    }
}
