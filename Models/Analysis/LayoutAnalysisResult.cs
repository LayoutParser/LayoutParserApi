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
}