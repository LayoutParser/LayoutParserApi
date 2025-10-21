using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Models.Generation
{
    public class GeneratedDataResult
    {
        public bool Success { get; set; }
        public List<string> GeneratedLines { get; set; } = new();
        public string ErrorMessage { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public int TotalRecords { get; set; }
        public Layout UsedLayout { get; set; }
        public Dictionary<string, object> GenerationMetadata { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<FieldGenerationStats> FieldStats { get; set; } = new();
    }

    public class FieldGenerationStats
    {
        public string FieldName { get; set; }
        public string GenerationStrategy { get; set; }
        public int GeneratedCount { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public List<string> SampleValues { get; set; } = new();
    }

    public class SyntheticDataRequest
    {
        public Layout Layout { get; set; }
        public int NumberOfRecords { get; set; }
        public List<ParsedField> SampleRealData { get; set; } = new();
        public ExcelDataContext ExcelContext { get; set; }
        public Dictionary<string, object> CustomRules { get; set; } = new();
        public bool UseAI { get; set; } = true;
        public string AIPrompt { get; set; }
    }
}
