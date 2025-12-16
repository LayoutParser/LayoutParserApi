namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    /// <summary>
    /// Resultado do pipeline de transformação
    /// </summary>
    public class TransformationPipelineResult
    {
        public bool Success { get; set; }
        public string TransformedXml { get; set; }
        public string TclPath { get; set; }
        public string XslPath { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public Dictionary<string, string> StepResults { get; set; } = new();
        public Dictionary<int, string> SegmentMappings { get; set; } = new();
    }
}
