namespace LayoutParserApi.Models.Validation
{
    public class ValidationSuggestion
    {
        public string Type { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string FieldName { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string Confidence { get; set; } = "Medium";
    }
}
