namespace LayoutParserApi.Models.Logging
{
    /// <summary>
    /// Resultado paginado de GET api/logs.
    /// </summary>
    public class PagedLogResult
    {
        public List<UnifiedLogEntry> Items { get; set; } = new();

        public int TotalCount { get; set; }

        public int Page { get; set; }

        public int PageSize { get; set; }
    }
}
