using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Logging;
using LayoutParserApi.Tests.Infra;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Logging
{
    /// <summary>
    /// Cenário 1 do handoff de hardening: o regex do leitor precisa aceitar linha SEM
    /// <c>[Corr:...]</c>.
    /// </summary>
    /// <remarks>
    /// Regressão real (QA/Quinn, 2026-07-30): o job de métricas roda fora da API, com um Serilog
    /// próprio cujo outputTemplate não tem o campo CorrelationId. Com o grupo obrigatório no regex,
    /// 100% das linhas "Geracao concluida." eram tratadas como CONTINUAÇÃO da entrada anterior e o
    /// painel do Gap 3 respondia vazio — sem erro nenhum, em silêncio. Os dois lados pareciam certos
    /// isoladamente; só a leitura de linha real pega isso.
    /// </remarks>
    public class UnifiedLogReaderServiceTests
    {
        [Fact]
        public async Task Linha_sem_correlation_id_e_lida_como_entrada_propria()
        {
            using var temp = new TempLogDirectory();

            // 1ª linha: formato do job da VM (sem [Corr:]). 2ª: formato da própria API (com [Corr:]).
            temp.WriteRawLines(
                "layoutparserai.log",
                "[2026-07-27 03:41:12.000] [INF] [Src:AiMetrics] Geracao concluida. Layout=NFe\\4.00\\NFe009 Sucesso=true",
                "[2026-07-27 03:41:13.000] [INF] [Corr:9f2c] [Src:AiMetrics] Cypress validado. Layout=NFe\\4.00\\NFe009 CypressValidado=true");

            var reader = new UnifiedLogReaderService(temp.BuildConfiguration(), NullLogger<UnifiedLogReaderService>.Instance);

            var result = await reader.GetLogsAsync(new UnifiedLogFilter { Source = "AiMetrics", PageSize = 500 });

            Assert.Equal(2, result.TotalCount);

            var geracao = result.Items.Single(e => e.Message.StartsWith("Geracao concluida.", StringComparison.Ordinal));
            Assert.Null(geracao.CorrelationId);
            Assert.Equal("AiMetrics", geracao.Source);
            Assert.Equal(new DateTime(2026, 7, 27, 3, 41, 12), geracao.Timestamp);

            // A linha seguinte não pode ter sido "engolida" como continuação da anterior.
            Assert.DoesNotContain("Cypress validado.", geracao.Message, StringComparison.Ordinal);

            var cypress = result.Items.Single(e => e.Message.StartsWith("Cypress validado.", StringComparison.Ordinal));
            Assert.Equal("9f2c", cypress.CorrelationId);
        }

        [Fact]
        public async Task Diretorio_de_log_inexistente_degrada_para_vazio_sem_lancar()
        {
            using var temp = new TempLogDirectory();
            temp.Dispose(); // some com a pasta antes da leitura

            var reader = new UnifiedLogReaderService(temp.BuildConfiguration(), NullLogger<UnifiedLogReaderService>.Instance);

            var result = await reader.GetLogsAsync(new UnifiedLogFilter { Source = "AiMetrics" });

            Assert.Equal(0, result.TotalCount);
            Assert.Empty(result.Items);
        }
    }
}
