using System.Globalization;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using Serilog.Context;

namespace LayoutParserApi.Services.Logging
{
    /// <summary>
    /// Ingestão de gerações de IA vindas de fora do processo da API (a VM Linux de métricas de IA —
    /// o IP dela muda por DHCP, confirme o atual no runbook operacional).
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

        // ✅ FIX (QA/Quinn 2026-07-31): tetos de tamanho por campo, mesmo padrão do endpoint irmão
        // (POST cypress-result, 500/20/1000 chars). Sem eles um Layout de 200.000 chars era aceito e
        // gravava 200 KB numa linha só — e a retenção do log é de ~20 MB (FileSizeLimitKB ×
        // RetainedFileCountLimit) com o leitor abrindo só os 3 arquivos mais recentes por fonte, ou
        // seja: um payload gigante EVICTA histórico real de gerações.
        private const int MaxLayoutLength = 500;
        private const int MaxModeloLength = 100;
        private const int MaxCStatLength = 20;

        // Teto do trecho de Layout ecoado nas mensagens de motivo — a resposta e o log não podem
        // carregar de volta o payload gigante que a validação acabou de recusar.
        private const int MaxTrechoMotivo = 80;

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

        public string? ValidarContratoDoLote(IReadOnlyList<AiMetricsGenerationIngestRequest>? geracoes)
        {
            // Lote vazio/nulo não é violação de contrato — o controller já o barra antes daqui.
            if (geracoes is null || geracoes.Count == 0)
                return null;

            var semTimestamp = geracoes.Count(g => g is not null && !TemTimestamp(g));

            if (semTimestamp == 0)
                return null;

            return $"Campo 'timestamp' é obrigatório em todos os itens do lote ({semTimestamp} de "
                + $"{geracoes.Count} sem timestamp). Sem ele o reenvio do mesmo lote duplica as "
                + "gerações no painel, porque a leitura colapsa duplicatas por (Layout, Timestamp).";
        }

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
        /// Valida os dois campos realmente inegociáveis: o <c>Layout</c>, que é a chave de junção com
        /// o resultado do Cypress, e o <c>Timestamp</c>, que é a outra metade da chave de dedup da
        /// leitura. Espaço em branco no meio do Layout truncaria o valor silenciosamente na
        /// tokenização do leitor (Chave=Valor separada por espaço) — e junção que falha em silêncio
        /// é exatamente o modo de falha que este endpoint existe pra evitar. Melhor recusar alto do
        /// que gravar uma chave mutilada; por isso Layout acima do teto é RECUSADO (e não truncado,
        /// ao contrário dos campos não-chave, ver <see cref="Texto"/>/<see cref="Nulavel"/>).
        /// </summary>
        /// <remarks>
        /// O <c>Timestamp</c> é checado aqui além de em <see cref="ValidarContratoDoLote"/>: aquele
        /// existe pro endpoint responder 400 ao produtor quebrado; este garante que nenhuma chamada
        /// direta ao serviço (teste, futuro job in-process) volte a gravar geração sem instante real
        /// e reabra a duplicação no painel.
        /// </remarks>
        private static string? ValidarItem(AiMetricsGenerationIngestRequest? geracao)
        {
            if (geracao is null)
                return "Item nulo no lote.";

            if (string.IsNullOrWhiteSpace(geracao.Layout))
                return "Campo 'layout' é obrigatório.";

            if (geracao.Layout.Length > MaxLayoutLength)
                return $"Layout '{Resumir(geracao.Layout)}' excede o limite de {MaxLayoutLength} caracteres ({geracao.Layout.Length}).";

            if (geracao.Layout.Any(char.IsWhiteSpace))
                return $"Layout '{Resumir(geracao.Layout)}' contém espaço em branco — não é possível preservar a chave de junção.";

            if (!TemTimestamp(geracao))
                return $"Campo 'timestamp' é obrigatório (layout '{Resumir(geracao.Layout)}').";

            return null;
        }

        private static bool TemTimestamp(AiMetricsGenerationIngestRequest geracao)
            => geracao.Timestamp.HasValue && geracao.Timestamp.Value != default;

        private static string Resumir(string valor)
            => valor.Length <= MaxTrechoMotivo ? valor : valor[..MaxTrechoMotivo] + "…";

        /// <summary>
        /// Grava a linha canônica. O <c>Layout</c> vai byte-a-byte como veio (sem Trim, sem troca de
        /// barra, sem mudar caixa): o outputTemplate da API usa <c>{Message:lj}</c>, ou seja, string
        /// renderizada literal, sem aspas nem escape — as barras invertidas sobrevivem intactas,
        /// igual ao que o job grava na VM.
        /// </summary>
        private void EscreverLinhaGeracao(AiMetricsGenerationIngestRequest geracao)
        {
            // ValidarItem já garantiu que o instante veio no payload (o "!" é seguro por isso) —
            // este método nunca inventa horário.
            var timestamp = NormalizarParaBaseDoLog(geracao.Timestamp!.Value);

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
                    Texto(geracao.Modelo, MaxModeloLength),
                    Numero(geracao.TokensPorSegundo),
                    geracao.TamanhoPromptChars.ToString(CultureInfo.InvariantCulture),
                    Numero(geracao.DuracaoSegundos),
                    Numero(geracao.SimilaridadeFewShot),
                    Numero(geracao.TagOverlapRatio),
                    Numero(geracao.TextSimilarityRatio),
                    geracao.XsdValido,
                    geracao.CypressValidado,
                    Nulavel(geracao.CStatPollux, MaxCStatLength),
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
        /// a falhar em silêncio.
        /// </summary>
        /// <remarks>
        /// ✅ FIX (QA/Quinn 2026-07-31): NÃO existe mais fallback pra <c>DateTime.Now</c> quando o
        /// campo vem ausente. Era o que quebrava a idempotência prometida no XML doc do endpoint: o
        /// mesmo lote reenviado ganhava instantes diferentes (medido: 11:35:29.505 vs 11:35:29.569),
        /// a dedup por (Layout, Timestamp) não colapsava nada e o painel exibia a geração duas
        /// vezes. Agora o instante é obrigatório e validado antes (ver ValidarItem) — idempotência
        /// que depende de sorte de timing não é idempotência.
        /// </remarks>
        private static DateTime NormalizarParaBaseDoLog(DateTime timestamp)
        {
            // Kind=Local (offset explícito) ou Unspecified (sem fuso) já chegam na base do log.
            return timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
        }

        // ✅ NaN/Infinity viram 0: o JSON da API aceita esses literais
        // (JsonNumberHandling.AllowNamedFloatingPointLiterals em Program.cs) e não há leitura útil
        // deles num painel de métricas — mesmo saneamento que o leitor faz do outro lado.
        private static string Numero(double valor)
            => (double.IsNaN(valor) || double.IsInfinity(valor) ? 0d : valor).ToString(CultureInfo.InvariantCulture);

        // Campos não-chave com espaço são normalizados (não recusados): truncar "qwen 2.5" no
        // primeiro espaço seria pior, e nenhum deles participa da junção. Pelo mesmo motivo, aqui o
        // excesso de tamanho TRUNCA (não recusa o item): perder o sufixo do nome do modelo é
        // aceitável, perder a rodada inteira por causa dele não — o oposto da regra do Layout.
        private static string Texto(string? valor, int maxLength)
            => string.IsNullOrWhiteSpace(valor) ? string.Empty : SemEspacos(Truncar(valor, maxLength));

        private static string? Nulavel(string? valor, int maxLength)
            => string.IsNullOrWhiteSpace(valor) ? null : SemEspacos(Truncar(valor, maxLength));

        private static string Truncar(string valor, int maxLength)
            => valor.Length <= maxLength ? valor : valor[..maxLength];

        private static string SemEspacos(string valor)
            => valor.Any(char.IsWhiteSpace)
                ? new string(valor.Select(c => char.IsWhiteSpace(c) ? '_' : c).ToArray())
                : valor;
    }
}
