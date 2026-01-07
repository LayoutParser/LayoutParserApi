namespace LayoutParserApi.Models.Generation
{
    public class ExcelProcessingResult
    {
        public bool Success { get; set; }
        public ExcelDataContext DataContext { get; set; }
        public List<FieldMapping> FieldMappings { get; set; } = new();
        public string ErrorMessage { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}