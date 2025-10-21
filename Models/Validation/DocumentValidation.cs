namespace LayoutParserApi.Models.Validation
{
    public class DocumentValidation
    {
        public bool IsValid { get; set; }
        public bool HasErrors { get; set; }
        public bool HasWarnings { get; set; }
        public string OverallStatus { get; set; } = "Unknown";
        public List<string> MissingRequiredLines { get; set; } = new();
        public List<string> StructuralErrors { get; set; } = new();
        public List<string> ValidationWarnings { get; set; } = new();
        public List<string> CriticalErrors { get; set; } = new();
        public bool IsStructurallyValid { get; set; }
        public bool IsBusinessValid { get; set; }
        public bool IsCompliant { get; set; }
        public int ComplianceScore { get; set; }
        public int StructureScore { get; set; }
        public int BusinessScore { get; set; }
        public List<ValidationSuggestion> Suggestions { get; set; } = new();
    }
}
