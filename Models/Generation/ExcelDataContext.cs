namespace LayoutParserApi.Models.Generation
{
    public class ExcelDataContext
    {
        public Dictionary<string, List<string>> ColumnData { get; set; } = new();
        public List<string> Headers { get; set; } = new();
        public Dictionary<string, string> ColumnTypes { get; set; } = new();
        public int RowCount { get; set; }
        public string FileName { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}