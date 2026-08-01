using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Tests.Infra
{
    /// <summary>
    /// Leitor unificado de mentira: devolve entradas montadas na memória, sem tocar em disco.
    /// Serve pros testes que exercitam o PARSE/MERGE do <c>AiMetricsReaderService</c>, onde o
    /// arquivo real seria só ruído (o pipeline de arquivo tem teste próprio, ver
    /// <c>AiMetricsRoundTripTests</c>).
    /// </summary>
    internal sealed class FakeUnifiedLogReaderService : IUnifiedLogReaderService
    {
        private readonly List<UnifiedLogEntry> _entries;

        public FakeUnifiedLogReaderService(params UnifiedLogEntry[] entries)
        {
            _entries = entries.ToList();
        }

        /// <summary>Entrada AiMetrics já "parseada pelo leitor de arquivo" (prefixo da linha fora).</summary>
        public static UnifiedLogEntry Entrada(string message, DateTime timestamp)
            => new()
            {
                Source = "AiMetrics",
                Level = "INF",
                CorrelationId = null,
                Message = message,
                Timestamp = timestamp
            };

        public Task<PagedLogResult> GetLogsAsync(UnifiedLogFilter filter)
        {
            filter ??= new UnifiedLogFilter();

            IEnumerable<UnifiedLogEntry> query = _entries;

            if (!string.IsNullOrWhiteSpace(filter.Source))
                query = query.Where(e => string.Equals(e.Source, filter.Source, StringComparison.OrdinalIgnoreCase));

            // Mesma ordenação do serviço real (mais recente primeiro) — a paginação do chamador
            // depende dela.
            var all = query.OrderByDescending(e => e.Timestamp).ToList();
            var page = filter.Page < 1 ? 1 : filter.Page;
            var pageSize = filter.PageSize < 1 ? 1 : filter.PageSize;

            return Task.FromResult(new PagedLogResult
            {
                Items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                TotalCount = all.Count,
                Page = page,
                PageSize = pageSize
            });
        }
    }
}
