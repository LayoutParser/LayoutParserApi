using LayoutParserApi.Models.Logging;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Lê e parseia as linhas de log estruturado do job de métricas de IA
    /// (ai/XslSynth --mode=metrics-batch, Source=AiMetrics), reaproveitando
    /// <see cref="IUnifiedLogReaderService"/> para a leitura bruta do arquivo. Serve os Endpoints
    /// do Gap 3 (docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md).
    /// </summary>
    public interface IAiMetricsReaderService
    {
        Task<PagedAiMetricsGenerationsResult> GetGenerationsAsync(AiMetricsGenerationFilter filter);

        Task<AiMetricsSummary> GetSummaryAsync(DateTime? de, DateTime? ate);
    }
}
