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

    public class FieldMapping
    {
        public string LayoutFieldName { get; set; }
        public string ExcelColumnName { get; set; }
        public double Confidence { get; set; }
        public List<string> SampleMappings { get; set; } = new();
        public string MappingReason { get; set; }
        public bool IsAutoDetected { get; set; }
    }

    public class ExcelProcessingResult
    {
        public bool Success { get; set; }
        public ExcelDataContext DataContext { get; set; }
        public List<FieldMapping> FieldMappings { get; set; } = new();
        public string ErrorMessage { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}
