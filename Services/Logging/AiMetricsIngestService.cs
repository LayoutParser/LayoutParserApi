using System.Globalization;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using Serilog.Context;

namespace LayoutParserApi.Services.Logging
{
    /// <summary>
    /// Ingestão de gerações de IA vindas de fora do processo da API (VM Linux 172.25.32.31).
    /// Grava exatamente a MESMA linha Serilog que o job ai/XslSynth --mode=metrics-batch grava no
    /// log local dele (<c>MetricsBatchRunner.LogCaso</c>) — mesmo prefixo, mesmos pares Chave=Valor,
    /// mesmo Source=AiMetrics — de modo que o <see cref="AiMetricsReaderService"/> a enxergue sem
    /// nenhum caminho de leitura novo. É o fix do bug em que o painel do Gap 3 lia um diretório
    /// (Windows) que nunca recebia as gerações (§A4 de handoff-job2-cypress-batch.md).
    /// </summary>
    public class AiMetricsIngestService : IAiMetricsIngestService
    {
        private const string AiMetricsSource = "AiMetrics";

        // Lote real hoje é ~54 casos; o teto só existe pra barrar payload absurdo.
        private const int MaxLoteSize = 1000;

        // Não devolve um motivo por item num lote grande — resposta enxuta, o resto vai pro log.
        private const int MaxMotivos = 20;

        // Mesma base/precisão do timestamp da própria linha de log da API
        // ([{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] do outputTemplate em Program.cs), sem fuso e sem
        // espaço — espaço quebraria a tokenização Chave=Valor do leitor.
        private const string FormatoTimestamp = "yyyy-MM-ddTHH:mm:ss.fff";

        private readonly ILogger<AiMetricsIngestService> _logger;

        public AiMetricsIngestService(ILogger<AiMetricsIngestService> logger)
        {
            _logger = logger;
        }

        public int TamanhoMaximoLote => MaxLoteSize;

        public AiMetricsIngestResult IngestGenerations(IReadOnlyList<AiMetricsGenerationIngestRequest> geracoes)
        {
            var resultado = new AiMetricsIngestResult { Recebidos = geracoes?.Count ?? 0 };

            if (geracoes is null || geracoes.Count == 0)
                return resultado;

            foreach (var geracao in geracoes)
            {
                try
                {
                    var motivo = ValidarItem(geracao);
                    if (motivo != null)
                    {
                        RegistrarIgnorado(resultado, motivo);
                        continue;
                    }

                    EscreverLinhaGeracao(geracao!);
                    resultado.Ingeridos++;
                }
                catch (Exception ex)
                {
                    // ✅ Degrada por item: uma falha de gravação (sink de arquivo indisponível,
                    // disco cheio) não invalida o restante do lote nem derruba o request.
                    _logger.LogWarning(ex, "Falha ao gravar geração de métricas de IA do layout {Layout}, item ignorado", geracao?.Layout);
                    RegistrarIgnorado(resultado, $"Falha ao gravar a geração do layout '{geracao?.Layout}'.");
                }
            }

            return resultado;
        }

        /// <summary>
        /// Valida o único campo realmente inegociável: o <c>Layout</c>, que é a chave de junção com
        /// o resultado do Cypress. Espaço em branco no meio dele truncaria o valor silenciosamente
        /// na tokenização do leitor (Chave=Valor separada por espaço) — e junção que falha em
        /// silêncio é exatamente o modo de falha que este endpoint existe pra evitar. Melhor
        /// recusar alto do que gravar uma chave mutilada.
        /// </summary>
        private static string? ValidarItem(AiMetricsGenerationIngestRequest? geracao)
        {
            if (geracao is null)
                return "Item nulo no lote.";

            if (string.IsNullOrWhiteSpace(geracao.Layout))
                return "Campo 'layout' é obrigatório.";

            if (geracao.Layout.Any(char.IsWhiteSpace))
                return $"Layout '{geracao.Layout}' contém espaço em branco — não é possível preservar a chave de junção.";

            return null;
        }

        /// <summary>
        /// Grava a linha canônica. O <c>Layout</c> vai byte-a-byte como veio (sem Trim, sem troca de
        /// barra, sem mudar caixa): o outputTemplate da API usa <c>{Message:lj}</c>, ou seja, string
        /// renderizada literal, sem aspas nem escape — as barras invertidas sobrevivem intactas,
        /// igual ao que o job grava na VM.
        /// </summary>
        private void EscreverLinhaGeracao(AiMetricsGenerationIngestRequest geracao)
        {
            var timestamp = NormalizarParaBaseDoLog(geracao.Timestamp);

            using (LogContext.PushProperty("Source", AiMetricsSource))
            {
                // Template idêntico ao de MetricsBatchRunner.LogCaso + o campo Timestamp (aditivo:
                // o parser é por pares Chave=Valor, independente de ordem, e ignora chave ausente).
                _logger.LogInformation(
                    "Geracao concluida. Layout={Layout} Modelo={Model} TokensPorSegundo={TokensPerSecond} "
                    + "TamanhoPromptChars={PromptChars} DuracaoSegundos={DurationSeconds} "
                    + "SimilaridadeFewShot={FewShotSimilarity} TagOverlapRatio={TagOverlapRatio} "
                    + "TextSimilarityRatio={TextSimilarityRatio} XsdValido={XsdValid} "
                    + "CypressValidado={CypressValidated} CStatPollux={CStatPollux} Sucesso={Sucesso} "
                    + "Timestamp={Timestamp}",
                    geracao.Layout,
                    Texto(geracao.Modelo),
                    Numero(geracao.TokensPorSegundo),
                    geracao.TamanhoPromptChars.ToString(CultureInfo.InvariantCulture),
                    Numero(geracao.DuracaoSegundos),
                    Numero(geracao.SimilaridadeFewShot),
                    Numero(geracao.TagOverlapRatio),
                    Numero(geracao.TextSimilarityRatio),
                    geracao.XsdValido,
                    geracao.CypressValidado,
                    Nulavel(geracao.CStatPollux),
                    geracao.Sucesso,
                    timestamp.ToString(FormatoTimestamp, CultureInfo.InvariantCulture));
            }
        }

        private static void RegistrarIgnorado(AiMetricsIngestResult resultado, string motivo)
        {
            resultado.Ignorados++;
            if (resultado.Motivos.Count < MaxMotivos)
                resultado.Motivos.Add(motivo);
        }

        /// <summary>
        /// Converte o instante informado para a MESMA base de tempo do arquivo de log (hora local do
        /// servidor — o outputTemplate grava sem fuso). Sem isso, um horário UTC gravado cru ficaria
        /// 3h à frente das linhas vizinhas e o merge do Cypress (<c>cypress >= geracao</c>) passaria
        /// a falhar em silêncio. Sem valor (ou <c>default</c>) = agora.
        /// </summary>
        private static DateTime NormalizarParaBaseDoLog(DateTime? timestamp)
        {
            if (!timestamp.HasValue || timestamp.Value == default)
                return DateTime.Now;

            // Kind=Local (offset explícito) ou Unspecified (sem fuso) já chegam na base do log.
            return timestamp.Value.Kind == DateTimeKind.Utc
                ? timestamp.Value.ToLocalTime()
                : timestamp.Value;
        }

        // ✅ NaN/Infinity viram 0: o JSON da API aceita esses literais
        // (JsonNumberHandling.AllowNamedFloatingPointLiterals em Program.cs) e não há leitura útil
        // deles num painel de métricas — mesmo saneamento que o leitor faz do outro lado.
        private static string Numero(double valor)
            => (double.IsNaN(valor) || double.IsInfinity(valor) ? 0d : valor).ToString(CultureInfo.InvariantCulture);

        // Campos não-chave com espaço são normalizados (não recusados): truncar "qwen 2.5" no
        // primeiro espaço seria pior, e nenhum deles participa da junção.
        private static string Texto(string? valor)
            => string.IsNullOrWhiteSpace(valor) ? string.Empty : SemEspacos(valor);

        private static string? Nulavel(string? valor)
            => string.IsNullOrWhiteSpace(valor) ? null : SemEspacos(valor);

        private static string SemEspacos(string valor)
            => valor.Any(char.IsWhiteSpace)
                ? new string(valor.Select(c => char.IsWhiteSpace(c) ? '_' : c).ToArray())
                : valor;
    }
}
