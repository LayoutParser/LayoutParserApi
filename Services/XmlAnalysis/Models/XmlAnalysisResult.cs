namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    public class XmlAnalysisResult
    {
        public bool Success { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int TotalElements { get; set; }
        public int TotalAttributes { get; set; }
        public int Depth { get; set; }
        public Dictionary<string, object> ValidationDetails { get; set; } = new();
    }
}
