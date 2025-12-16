using static LayoutParserApi.Services.XmlAnalysis.MqSeriesToXmlTransformer;

namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    /// <summary>
    /// Resultado da transformação
    /// </summary>
    public class TransformationResult
    {
        public bool Success { get; set; }
        public string TransformedXml { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public Dictionary<int, SegmentMapping> SegmentMappings { get; set; } = new();
    }
}
