namespace LayoutParserApi.Models.Generation
{
    public class FieldMapping
    {
        public string LayoutFieldName { get; set; }
        public string ExcelColumnName { get; set; }
        public double Confidence { get; set; }
        public List<string> SampleMappings { get; set; } = new();
        public string MappingReason { get; set; }
        public bool IsAutoDetected { get; set; }
    }
}