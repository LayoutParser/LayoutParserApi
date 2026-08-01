using System.Globalization;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Logging;
using LayoutParserApi.Tests.Infra;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Logging
{
    /// <summary>
    /// Cenários 2 a 5 do handoff de hardening — os quatro defeitos de CORRUPÇÃO DE DADO que o QA
    /// corrigiu no painel de métricas de IA. Todos passavam despercebidos porque o painel continuava
    /// respondendo 200: o número é que estava errado, e número errado em apresentação de diretoria é
    /// a severidade máxima deste projeto.
    /// </summary>
    public class AiMetricsReaderServiceTests
    {
        private const string LayoutCTe = @"CTe\2.00a\CTe200_CancCTe_NeogridToSefaz";

        private static AiMetricsReaderService CriarReader(FakeUnifiedLogReaderService fake)
            => new(fake, NullLogger<AiMetricsReaderService>.Instance);

        private static string LinhaGeracao(string layout, DateTime timestamp, bool sucesso = true)
            => $"Geracao concluida. Layout={layout} Modelo=qwen2.5-coder:7b TokensPorSegundo=3.29 "
             + "TamanhoPromptChars=1200 DuracaoSegundos=42 SimilaridadeFewShot=0.5 TagOverlapRatio=0.6 "
             + "TextSimilarityRatio=0.7 XsdValido=null CypressValidado=null CStatPollux=null "
             + $"Sucesso={(sucesso ? "true" : "false")} "
             + $"Timestamp={timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)}";

        /// <summary>
        /// Cenário 2 — texto livre com "=" na observação não pode forjar campo estruturado.
        /// Regressão real: a observação honesta "esperava CStatPollux=100 e veio 110" fazia o painel
        /// exibir cStat 100 (aceite) para uma REJEIÇÃO, por causa de split(' ') + last-wins.
        /// </summary>
        [Fact]
        public async Task Observacao_com_igual_nao_forja_o_cstat_da_geracao()
        {
            var geracaoEm = new DateTime(2026, 7, 27, 10, 0, 0);
            var cypressEm = new DateTime(2026, 7, 27, 12, 0, 0);

            var fake = new FakeUnifiedLogReaderService(
                FakeUnifiedLogReaderService.Entrada(LinhaGeracao(LayoutCTe, geracaoEm), geracaoEm),
                FakeUnifiedLogReaderService.Entrada(
                    $"Cypress validado. Layout={LayoutCTe} CypressValidado=false CStatPollux=110 "
                    + "Observacao=Rejeitado: esperava CStatPollux=100 e veio 110",
                    cypressEm));

            var page = await CriarReader(fake).GetGenerationsAsync(new AiMetricsGenerationFilter());

            var geracao = Assert.Single(page.Items);
            Assert.Equal("110", geracao.CStatPollux);
            Assert.False(geracao.CypressValidado);
        }

        /// <summary>
        /// Cenário 3 — rejeição do Pollux não pode ser contada como autorização. A API não re-deriva
        /// aceite a partir do código cStat (110/225 são rejeição, e os códigos de aceite variam por
        /// tipo de evento): quem sabe disso é o cliente Cypress, via CypressValidado.
        /// </summary>
        [Fact]
        public async Task Rejeicao_do_pollux_nao_entra_no_card_de_autorizados()
        {
            var geracaoEm = new DateTime(2026, 7, 27, 10, 0, 0);
            var cypressEm = new DateTime(2026, 7, 27, 12, 0, 0);

            var fake = new FakeUnifiedLogReaderService(
                FakeUnifiedLogReaderService.Entrada(LinhaGeracao(LayoutCTe, geracaoEm), geracaoEm),
                FakeUnifiedLogReaderService.Entrada(
                    $"Cypress validado. Layout={LayoutCTe} CypressValidado=false CStatPollux=110 Observacao=rejeitado",
                    cypressEm));

            var summary = await CriarReader(fake).GetSummaryAsync(null, null);

            Assert.Equal(1, summary.TotalGeracoes);
            Assert.Equal(0, summary.TotalCStatAutorizado);
            Assert.Equal(0, summary.TotalCypressValidado);
        }

        /// <summary>
        /// Cenário 4 — o dataset re-roda toda semana, então o MESMO layout aparece em várias rodadas.
        /// Um único POST de resultado marcava TODAS as rodadas históricas e 1 validação virava N no
        /// card do painel. O resultado pertence à geração mais recente anterior ao POST.
        /// </summary>
        [Fact]
        public async Task Merge_do_cypress_nao_contamina_rodadas_antigas_do_mesmo_layout()
        {
            var rodadaAntiga = new DateTime(2026, 7, 20, 10, 0, 0);
            var rodadaRecente = new DateTime(2026, 7, 27, 10, 0, 0);
            var cypressEm = new DateTime(2026, 7, 27, 12, 0, 0);

            var fake = new FakeUnifiedLogReaderService(
                FakeUnifiedLogReaderService.Entrada(LinhaGeracao(LayoutCTe, rodadaAntiga), rodadaAntiga),
                FakeUnifiedLogReaderService.Entrada(LinhaGeracao(LayoutCTe, rodadaRecente), rodadaRecente),
                FakeUnifiedLogReaderService.Entrada(
                    $"Cypress validado. Layout={LayoutCTe} CypressValidado=true CStatPollux=135 Observacao=ok",
                    cypressEm));

            var page = await CriarReader(fake).GetGenerationsAsync(new AiMetricsGenerationFilter());

            Assert.Equal(2, page.TotalCount);

            var recente = page.Items.Single(g => g.Timestamp == rodadaRecente);
            Assert.True(recente.CypressValidado);
            Assert.Equal("135", recente.CStatPollux);

            var antiga = page.Items.Single(g => g.Timestamp == rodadaAntiga);
            Assert.Null(antiga.CypressValidado);
            Assert.Null(antiga.CStatPollux);
        }

        /// <summary>
        /// Cenário 5 — o Job 2 posta o resultado no dia SEGUINTE ao da geração. Se o recorte de
        /// período fosse empurrado pro leitor de log, filtrar o sábado descartaria a linha de
        /// domingo e o merge sumiria do painel. Lê tudo → faz o merge → só então recorta.
        /// </summary>
        [Fact]
        public async Task Merge_do_cypress_sobrevive_ao_recorte_de_periodo()
        {
            var geracaoEm = new DateTime(2026, 7, 27, 10, 0, 0);   // sábado: a rodada
            var cypressEm = new DateTime(2026, 7, 28, 9, 0, 0);    // domingo: o Job 2

            var fake = new FakeUnifiedLogReaderService(
                FakeUnifiedLogReaderService.Entrada(LinhaGeracao(LayoutCTe, geracaoEm), geracaoEm),
                FakeUnifiedLogReaderService.Entrada(
                    $"Cypress validado. Layout={LayoutCTe} CypressValidado=true CStatPollux=135 Observacao=ok",
                    cypressEm));

            var page = await CriarReader(fake).GetGenerationsAsync(new AiMetricsGenerationFilter
            {
                De = new DateTime(2026, 7, 27, 0, 0, 0),
                Ate = new DateTime(2026, 7, 27, 23, 59, 59)
            });

            var geracao = Assert.Single(page.Items);
            Assert.True(geracao.CypressValidado);
            Assert.Equal("135", geracao.CStatPollux);
        }

        /// <summary>
        /// Dedup da ingestão: reenviar o mesmo lote (retry de rede, replay de rodada) não pode
        /// inflar totais/médias. Colapsa por (Layout, Timestamp) — e é justamente por isso que o
        /// Timestamp virou obrigatório na ingestão.
        /// </summary>
        [Fact]
        public async Task Linhas_duplicadas_da_mesma_geracao_colapsam_em_um_item()
        {
            var geracaoEm = new DateTime(2026, 7, 27, 10, 0, 0);
            var linha = LinhaGeracao(LayoutCTe, geracaoEm);

            var fake = new FakeUnifiedLogReaderService(
                FakeUnifiedLogReaderService.Entrada(linha, geracaoEm),
                FakeUnifiedLogReaderService.Entrada(linha, geracaoEm));

            var page = await CriarReader(fake).GetGenerationsAsync(new AiMetricsGenerationFilter());

            Assert.Equal(1, page.TotalCount);
            Assert.Equal("CTe", Assert.Single(page.Items).DocType);
        }
    }
}
