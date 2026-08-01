using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Logging;
using LayoutParserApi.Tests.Infra;

namespace LayoutParserApi.Tests.Logging
{
    /// <summary>
    /// Cenário 6 do handoff (resiliência por item) mais as duas regras novas do hardening:
    /// <c>Timestamp</c> obrigatório (idempotência) e teto de tamanho de campo (retenção do log).
    /// </summary>
    public class AiMetricsIngestServiceTests
    {
        private const string LayoutValido = @"NFe\4.00\NFe009_4.00_EnvioNFe_NeoGridToSefaz";

        private static AiMetricsGenerationIngestRequest Geracao(string layout, DateTime? timestamp = null, string? modelo = "qwen2.5-coder:7b")
            => new()
            {
                Layout = layout,
                Modelo = modelo,
                Timestamp = timestamp ?? new DateTime(2026, 8, 1, 3, 41, 12, DateTimeKind.Utc),
                TokensPorSegundo = 3.29,
                TamanhoPromptChars = 1200,
                DuracaoSegundos = 42,
                SimilaridadeFewShot = 0.5,
                TagOverlapRatio = 0.6,
                TextSimilarityRatio = 0.7,
                Sucesso = true
            };

        /// <summary>Cenário 6 — um caso ruim no meio do lote não pode custar a rodada inteira.</summary>
        [Fact]
        public void Item_invalido_no_meio_do_lote_nao_derruba_o_lote()
        {
            var logger = new CapturingLogger<AiMetricsIngestService>();
            var service = new AiMetricsIngestService(logger);

            var resultado = service.IngestGenerations(new[]
            {
                Geracao(LayoutValido),
                Geracao("NFe\\4.00\\layout com espaco"),   // quebra a chave de junção
                Geracao(@"CTe\2.00a\CTe200_CancCTe")
            });

            Assert.Equal(3, resultado.Recebidos);
            Assert.Equal(2, resultado.Ingeridos);
            Assert.Equal(1, resultado.Ignorados);
            Assert.Single(resultado.Motivos);
            Assert.Equal(2, logger.Messages.Count);
        }

        /// <summary>
        /// Regressão A1: sem <c>Timestamp</c> a ingestão caía em <c>DateTime.Now</c>, o reenvio do
        /// mesmo lote produzia instantes diferentes e a dedup por (Layout, Timestamp) não colapsava
        /// nada — a geração aparecia duas vezes no painel.
        /// </summary>
        [Fact]
        public void Geracao_sem_timestamp_nao_e_gravada()
        {
            var logger = new CapturingLogger<AiMetricsIngestService>();
            var service = new AiMetricsIngestService(logger);

            var semTimestamp = Geracao(LayoutValido);
            semTimestamp.Timestamp = null;

            var resultado = service.IngestGenerations(new[] { semTimestamp });

            Assert.Equal(0, resultado.Ingeridos);
            Assert.Equal(1, resultado.Ignorados);
            Assert.Contains("timestamp", resultado.Motivos[0], StringComparison.OrdinalIgnoreCase);
            Assert.Empty(logger.Messages);
        }

        [Fact]
        public void Reenvio_do_mesmo_item_gera_linha_identica()
        {
            var logger = new CapturingLogger<AiMetricsIngestService>();
            var service = new AiMetricsIngestService(logger);
            var geracao = Geracao(LayoutValido);

            service.IngestGenerations(new[] { geracao });
            service.IngestGenerations(new[] { geracao });

            Assert.Equal(2, logger.Messages.Count);
            // Linha byte-a-byte idêntica = a dedup da leitura consegue colapsar. Era o que o
            // fallback pra DateTime.Now impedia.
            Assert.Equal(logger.Messages[0], logger.Messages[1]);
        }

        [Fact]
        public void ValidarContratoDoLote_recusa_lote_com_item_sem_timestamp()
        {
            var service = new AiMetricsIngestService(new CapturingLogger<AiMetricsIngestService>());

            var semTimestamp = Geracao(@"CTe\2.00a\CTe200_CancCTe");
            semTimestamp.Timestamp = null;

            var erro = service.ValidarContratoDoLote(new[] { Geracao(LayoutValido), semTimestamp });

            Assert.NotNull(erro);
            Assert.Contains("timestamp", erro, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidarContratoDoLote_aceita_lote_completo_e_lote_vazio()
        {
            var service = new AiMetricsIngestService(new CapturingLogger<AiMetricsIngestService>());

            Assert.Null(service.ValidarContratoDoLote(new[] { Geracao(LayoutValido) }));
            Assert.Null(service.ValidarContratoDoLote(Array.Empty<AiMetricsGenerationIngestRequest>()));
            Assert.Null(service.ValidarContratoDoLote(null));
        }

        /// <summary>
        /// Regressão A2: um <c>Layout</c> de 200.000 chars era aceito e gravava 200 KB numa linha só,
        /// evictando histórico real (a retenção do log é de ~20 MB e o leitor abre só os 3 arquivos
        /// mais recentes por fonte). E o motivo devolvido não pode ecoar o payload recusado.
        /// </summary>
        [Fact]
        public void Layout_gigante_e_recusado_sem_ecoar_o_payload_no_motivo()
        {
            var logger = new CapturingLogger<AiMetricsIngestService>();
            var service = new AiMetricsIngestService(logger);

            var resultado = service.IngestGenerations(new[] { Geracao(new string('L', 200_000)) });

            Assert.Equal(0, resultado.Ingeridos);
            Assert.Equal(1, resultado.Ignorados);
            Assert.Empty(logger.Messages);
            Assert.True(resultado.Motivos[0].Length < 300, $"Motivo ecoou o payload recusado ({resultado.Motivos[0].Length} chars).");
        }

        /// <summary>
        /// Campo não-chave excedendo o teto TRUNCA (não recusa): perder o sufixo do nome do modelo
        /// é aceitável, perder o caso da rodada por causa dele não — o oposto da regra do Layout.
        /// </summary>
        [Fact]
        public void Modelo_gigante_e_truncado_sem_derrubar_o_item()
        {
            var logger = new CapturingLogger<AiMetricsIngestService>();
            var service = new AiMetricsIngestService(logger);

            var resultado = service.IngestGenerations(new[] { Geracao(LayoutValido, modelo: new string('m', 5_000)) });

            Assert.Equal(1, resultado.Ingeridos);
            var mensagem = Assert.Single(logger.Messages);
            Assert.Contains($"Modelo={new string('m', 100)} TokensPorSegundo=", mensagem, StringComparison.Ordinal);
        }

        [Fact]
        public void Lote_nulo_ou_vazio_nao_lanca()
        {
            var service = new AiMetricsIngestService(new CapturingLogger<AiMetricsIngestService>());

            var vazio = service.IngestGenerations(Array.Empty<AiMetricsGenerationIngestRequest>());

            Assert.Equal(0, vazio.Recebidos);
            Assert.Equal(0, vazio.Ingeridos);
        }
    }
}
