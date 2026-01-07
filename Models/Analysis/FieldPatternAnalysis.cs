namespace LayoutParserApi.Models.Analysis
{
    public class FieldPatternAnalysis
    {
        public string FieldName { get; set; }
        public string DetectedPattern { get; set; }
        public List<string> CommonValues { get; set; } = new();
        public Dictionary<string, int> ValueFrequency { get; set; } = new();
        public string SuggestedGenerationStrategy { get; set; }
        public Dictionary<string, object> PatternMetadata { get; set; } = new();
    }
}