using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Logging;
using LayoutParserApi.Tests.Infra;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Logging
{
    /// <summary>
    /// Ida-e-volta REAL: ingestão grava com Serilog no arquivo (mesmo outputTemplate do Program.cs),
    /// e o painel relê pelo <c>UnifiedLogReaderService</c> + <c>AiMetricsReaderService</c>.
    /// </summary>
    /// <remarks>
    /// É o teste que fecha a fragilidade estrutural nº 1 do gate de QA: quem escreve e quem lê são
    /// dois lados que parecem certos isoladamente e só divergem no TEXTO da linha. Revisão de código
    /// não pega; só execução contra o formato real. Roda sempre em pasta temporária, nunca no
    /// diretório de log configurado.
    /// </remarks>
    public class AiMetricsRoundTripTests
    {
        [Fact]
        public async Task Geracao_ingerida_e_relida_pelo_painel_com_layout_e_instante_intactos()
        {
            using var temp = new TempLogDirectory();

            var instanteNaVm = new DateTime(2026, 8, 1, 3, 41, 12, DateTimeKind.Utc);
            var geracao = NovaGeracao(instanteNaVm);

            using (var factory = temp.CreateSerilogLoggerFactory())
            {
                var ingest = new AiMetricsIngestService(factory.CreateLogger<AiMetricsIngestService>());
                Assert.Equal(1, ingest.IngestGenerations(new[] { geracao }).Ingeridos);
            }

            var page = await LerPainel(temp);

            var item = Assert.Single(page.Items);
            // Barras invertidas e caixa preservadas byte-a-byte: normalizar de um lado só faria o
            // merge com o resultado do Cypress falhar em silêncio.
            Assert.Equal(geracao.Layout, item.Layout);
            Assert.Equal("NFe", item.DocType);
            Assert.Equal("qwen2.5-coder:7b", item.Modelo);
            // O instante enviado em UTC é gravado na base local do arquivo de log (que não tem fuso).
            Assert.Equal(instanteNaVm.ToLocalTime(), item.Timestamp);
            Assert.Equal(3.29, item.TokensPorSegundo);
            Assert.True(item.Sucesso);
            Assert.Null(item.XsdValido);
            Assert.Null(item.CypressValidado);
        }

        /// <summary>
        /// Idempotência ponta a ponta: reenviar o MESMO lote (retry de rede, replay de rodada)
        /// grava duas linhas — o log é append-only — mas o painel continua mostrando uma geração.
        /// </summary>
        [Fact]
        public async Task Reenvio_do_mesmo_lote_nao_duplica_a_geracao_no_painel()
        {
            using var temp = new TempLogDirectory();
            var geracao = NovaGeracao(new DateTime(2026, 8, 1, 3, 41, 12, DateTimeKind.Utc));

            using (var factory = temp.CreateSerilogLoggerFactory())
            {
                var ingest = new AiMetricsIngestService(factory.CreateLogger<AiMetricsIngestService>());
                ingest.IngestGenerations(new[] { geracao });
                ingest.IngestGenerations(new[] { geracao });
            }

            var page = await LerPainel(temp);

            Assert.Equal(1, page.TotalCount);
        }

        private static AiMetricsGenerationIngestRequest NovaGeracao(DateTime timestamp)
            => new()
            {
                Layout = @"NFe\4.00\NFe009_4.00_EnvioNFe_NeoGridToSefaz",
                Modelo = "qwen2.5-coder:7b",
                Timestamp = timestamp,
                TokensPorSegundo = 3.29,
                TamanhoPromptChars = 1200,
                DuracaoSegundos = 42,
                SimilaridadeFewShot = 0.5,
                TagOverlapRatio = 0.6,
                TextSimilarityRatio = 0.7,
                Sucesso = true
            };

        private static async Task<PagedAiMetricsGenerationsResult> LerPainel(TempLogDirectory temp)
        {
            var unified = new UnifiedLogReaderService(temp.BuildConfiguration(), NullLogger<UnifiedLogReaderService>.Instance);
            var reader = new AiMetricsReaderService(unified, NullLogger<AiMetricsReaderService>.Instance);

            return await reader.GetGenerationsAsync(new AiMetricsGenerationFilter());
        }
    }
}
