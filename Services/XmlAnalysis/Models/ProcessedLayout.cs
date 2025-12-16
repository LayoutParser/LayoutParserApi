namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    /// <summary>
    /// Layout processado
    /// </summary>
    public class ProcessedLayout
    {
        public string LayoutGuid { get; set; }
        public string LayoutName { get; set; }
        public string LayoutType { get; set; }
        public bool Success { get; set; }
        public List<string> GeneratedFiles { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
