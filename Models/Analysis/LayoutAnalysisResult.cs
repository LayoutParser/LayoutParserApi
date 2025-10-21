using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Models.Analysis
{
    public class LayoutAnalysisResult
    {
        public string LayoutType { get; set; }
        public string LayoutName { get; set; }
        public List<FieldAnalysis> Fields { get; set; } = new();
        public List<LineTypeAnalysis> LineTypes { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
        public int TotalFields { get; set; }
        public int TotalLines { get; set; }
    }

    public class FieldAnalysis
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public int Length { get; set; }
        public string Pattern { get; set; }
        public List<string> SampleValues { get; set; } = new();
        public bool IsSequential { get; set; }
        public bool IsRequired { get; set; }
        public string ValidationRules { get; set; }
        public string Alignment { get; set; }
        public int StartPosition { get; set; }
        public string Description { get; set; }
        public string LineName { get; set; }
    }

    public class LineTypeAnalysis
    {
        public string Name { get; set; }
        public string InitialValue { get; set; }
        public int MinimalOccurrence { get; set; }
        public int MaximumOccurrence { get; set; }
        public List<FieldAnalysis> Fields { get; set; } = new();
        public bool IsRequired { get; set; }
        public int TotalLength { get; set; }
    }

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
