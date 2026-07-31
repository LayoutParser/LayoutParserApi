using System.Globalization;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Logging
{
    /// <summary>
    /// Implementação do Gap 3 (docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md):
    /// parseia as linhas "Geracao concluida." do job ai/XslSynth --mode=metrics-batch
    /// (Source=AiMetrics) por cima do que <see cref="IUnifiedLogReaderService"/> já lê do arquivo
    /// de log unificado. Não duplica leitura/rotação de arquivo — só adiciona o parse dos
    /// pares Chave=Valor da mensagem estruturada.
    /// Linhas "Resumo do lote." são ignoradas: todos os campos pedidos pelos 2 endpoints deste
    /// Gap são deriváveis a partir dos casos individuais.
    /// </summary>
    public class AiMetricsReaderService : IAiMetricsReaderService
    {
        private const string AiMetricsSource = "AiMetrics";
        private const string GeracaoPrefix = "Geracao concluida.";

        // Página grande o suficiente pra volume real (~54 casos/rodada), evitando ida-e-volta
        // extra na maioria dos casos; o loop de paginação abaixo cobre o crescimento no tempo.
        private const int FetchPageSize = 500;

        // ✅ FIX (QA/Quinn): mesmo teto do UnifiedLogFilter irmão (UnifiedLogReaderService.cs) —
        // sem isso, um client mal-intencionado ou um bug de UI pode pedir uma página gigante.
        private const int MinPageSize = 1;
        private const int MaxPageSize = 500;

        private readonly IUnifiedLogReaderService _unifiedLogReaderService;
        private readonly ILogger<AiMetricsReaderService> _logger;

        public AiMetricsReaderService(IUnifiedLogReaderService unifiedLogReaderService, ILogger<AiMetricsReaderService> logger)
        {
            _unifiedLogReaderService = unifiedLogReaderService;
            _logger = logger;
        }

        public async Task<PagedAiMetricsGenerationsResult> GetGenerationsAsync(AiMetricsGenerationFilter filter)
        {
            filter ??= new AiMetricsGenerationFilter();
            var page = filter.Page < 1 ? 1 : filter.Page;
            var pageSize = filter.PageSize < MinPageSize ? MinPageSize : Math.Min(filter.PageSize, MaxPageSize);

            var all = await GetAllGenerationsAsync(filter.De, filter.Ate);

            IEnumerable<AiMetricsGeneration> filtered = all;

            if (!string.IsNullOrWhiteSpace(filter.Layout))
                filtered = filtered.Where(g => g.Layout.Contains(filter.Layout, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(filter.Modelo))
                filtered = filtered.Where(g => string.Equals(g.Modelo, filter.Modelo, StringComparison.OrdinalIgnoreCase));

            if (filter.Sucesso.HasValue)
                filtered = filtered.Where(g => g.Sucesso == filter.Sucesso.Value);

            var materialized = filtered.OrderByDescending(g => g.Timestamp).ToList();
            var totalCount = materialized.Count;
            var items = materialized.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return new PagedAiMetricsGenerationsResult
            {
                Success = true,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                Items = items
            };
        }

        public async Task<AiMetricsSummary> GetSummaryAsync(DateTime? de, DateTime? ate)
        {
            var all = await GetAllGenerationsAsync(de, ate);

            if (all.Count == 0)
            {
                // ✅ Job nunca rodou (ou ainda não tem log neste ambiente) — resumo vazio,
                // nunca 500 (degradação graciosa, ver dotnet-standards.md).
                return new AiMetricsSummary { Success = true };
            }

            var sucesso = all.Where(g => g.Sucesso).ToList();

            var porDocType = all
                .GroupBy(g => g.DocType)
                .Select(grp => new AiMetricsDocTypeSummary
                {
                    DocType = grp.Key,
                    Total = grp.Count(),
                    Sucesso = grp.Count(g => g.Sucesso),
                    TokensPorSegundoMedio = grp.Any(g => g.Sucesso)
                        ? grp.Where(g => g.Sucesso).Average(g => g.TokensPorSegundo)
                        : 0
                })
                .OrderBy(d => d.DocType)
                .ToList();

            return new AiMetricsSummary
            {
                Success = true,
                TotalGeracoes = all.Count,
                TotalSucesso = sucesso.Count,
                TotalFalhas = all.Count - sucesso.Count,
                TokensPorSegundoMedio = sucesso.Count > 0 ? sucesso.Average(g => g.TokensPorSegundo) : 0,
                TagOverlapMedio = sucesso.Count > 0 ? sucesso.Average(g => g.TagOverlapRatio) : 0,
                TextSimilarityMedia = sucesso.Count > 0 ? sucesso.Average(g => g.TextSimilarityRatio) : 0,
                TotalXsdValidado = all.Count(g => g.XsdValido == true),
                TotalCypressValidado = all.Count(g => g.CypressValidado == true),
                TotalCStatAutorizado = all.Count(g => !string.IsNullOrWhiteSpace(g.CStatPollux)),
                PorDocType = porDocType,
                UltimaRodada = all.Max(g => g.Timestamp)
            };
        }

        /// <summary>
        /// Busca TODAS as linhas AiMetrics (paginando o leitor unificado, que limita o retorno
        /// por página) já filtradas por data e parseadas. Falha na leitura/parse degrada pra
        /// lista vazia — nunca derruba os endpoints acima.
        /// </summary>
        private async Task<List<AiMetricsGeneration>> GetAllGenerationsAsync(DateTime? de, DateTime? ate)
        {
            var result = new List<AiMetricsGeneration>();

            try
            {
                var page = 1;
                int totalCount;

                do
                {
                    var raw = await _unifiedLogReaderService.GetLogsAsync(new Models.Logging.UnifiedLogFilter
                    {
                        Source = AiMetricsSource,
                        Page = page,
                        PageSize = FetchPageSize,
                        From = de,
                        To = ate
                    });

                    totalCount = raw.TotalCount;

                    foreach (var entry in raw.Items)
                    {
                        var parsed = TryParseGeracao(entry.Message, entry.Timestamp);
                        if (parsed != null)
                            result.Add(parsed);
                    }

                    page++;
                }
                while ((page - 1) * FetchPageSize < totalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada ao ler/parsear métricas de IA (AiMetrics)");
                return new List<AiMetricsGeneration>();
            }

            return result;
        }

        /// <summary>
        /// Parseia uma linha "Geracao concluida. Chave=Valor ...". Linhas "Resumo do lote." e
        /// qualquer coisa que não bata no formato esperado retornam null (ignoradas pelo
        /// chamador) — nunca lança, pra não derrubar o parse do restante do arquivo.
        /// </summary>
        private AiMetricsGeneration? TryParseGeracao(string message, DateTime timestamp)
        {
            if (string.IsNullOrWhiteSpace(message) || !message.StartsWith(GeracaoPrefix, StringComparison.Ordinal))
                return null;

            try
            {
                // ⚠️ Limitação conhecida (QA/Quinn): a tokenização assume que nenhum valor
                // (em especial "Layout=...") contém espaço — hoje válido pros paths reais do
                // dataset (backslash, sem espaço), mas se algum dia um layout com espaço no nome
                // passar a ser logado, ele seria truncado silenciosamente no primeiro espaço.
                // Baixa prioridade: não corrigido agora, documentado para revisão futura.
                var tokens = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var token in tokens)
                {
                    var separatorIndex = token.IndexOf('=');
                    if (separatorIndex <= 0)
                        continue; // parte do prefixo ("Geracao", "concluida."), sem '='

                    var key = token[..separatorIndex];
                    var value = token[(separatorIndex + 1)..];
                    fields[key] = value;
                }

                var layout = fields.GetValueOrDefault("Layout", string.Empty);

                return new AiMetricsGeneration
                {
                    Layout = layout,
                    DocType = DeriveDocType(layout),
                    Modelo = fields.GetValueOrDefault("Modelo", string.Empty),
                    Timestamp = timestamp,
                    TokensPorSegundo = ParseDouble(fields.GetValueOrDefault("TokensPorSegundo")),
                    TamanhoPromptChars = ParseInt(fields.GetValueOrDefault("TamanhoPromptChars")),
                    DuracaoSegundos = ParseDouble(fields.GetValueOrDefault("DuracaoSegundos")),
                    SimilaridadeFewShot = ParseDouble(fields.GetValueOrDefault("SimilaridadeFewShot")),
                    TagOverlapRatio = ParseDouble(fields.GetValueOrDefault("TagOverlapRatio")),
                    TextSimilarityRatio = ParseDouble(fields.GetValueOrDefault("TextSimilarityRatio")),
                    XsdValido = ParseNullableBool(fields.GetValueOrDefault("XsdValido")),
                    CypressValidado = ParseNullableBool(fields.GetValueOrDefault("CypressValidado")),
                    CStatPollux = ParseNullableString(fields.GetValueOrDefault("CStatPollux")),
                    Sucesso = ParseBool(fields.GetValueOrDefault("Sucesso"))
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao parsear linha AiMetrics, ignorada: {Message}", message);
                return null;
            }
        }

        private static string DeriveDocType(string layout)
        {
            if (string.IsNullOrWhiteSpace(layout))
                return string.Empty;

            var separatorIndex = layout.IndexOfAny(new[] { '\\', '/' });
            return separatorIndex > 0 ? layout[..separatorIndex] : layout;
        }

        // ✅ FIX (QA/Quinn): NumberStyles.Float aceita os literais "NaN"/"Infinity"/"-Infinity"
        // (independe do NumberStyles, é o NumberFormatInfo invariante) — plausível em produção
        // quando DuracaoSegundos ~0 (timeout instantâneo do Ollama), gerando TokensPorSegundo=NaN
        // no log real. System.Text.Json lança ArgumentException ao serializar NaN/Infinity, e
        // isso acontece DEPOIS do controller já ter retornado Ok(result) — fora do try/catch,
        // derrubando a resposta inteira com 500 cru. Normaliza pra 0: não há leitura útil de
        // NaN/Infinity num painel de métricas.
        private static double ParseDouble(string? raw)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return 0;

            return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
        }

        private static int ParseInt(string? raw)
            => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

        private static bool ParseBool(string? raw)
            => bool.TryParse(raw, out var value) && value;

        // ✅ O log grava literalmente "null" (via {Xsd=null}) quando o valor C# é null — trata
        // esse literal como ausência, não como erro de parse.
        private static bool? ParseNullableBool(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            return bool.TryParse(raw, out var value) ? value : null;
        }

        private static string? ParseNullableString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            return raw;
        }
    }
}
