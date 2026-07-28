namespace LayoutParserApi.Models.Logging
{
    /// <summary>
    /// Filtros e paginação aceitos por GET api/logs. Bind automático via [FromQuery].
    /// </summary>
    public class UnifiedLogFilter
    {
        public int Page { get; set; } = 1;

        public int PageSize { get; set; } = 50;

        /// <summary>Backend | Frontend | Lib | Decrypt.</summary>
        public string? Source { get; set; }

        /// <summary>Nível MÍNIMO (ex.: "Warning" retorna Warning + Error).</summary>
        public string? Level { get; set; }

        public string? CorrelationId { get; set; }

        public DateTime? From { get; set; }

        public DateTime? To { get; set; }

        /// <summary>Substring case-insensitive na mensagem.</summary>
        public string? Search { get; set; }
    }
}
