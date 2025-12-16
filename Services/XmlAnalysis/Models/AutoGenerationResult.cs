namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    /// <summary>
    /// Resultado da geração automática
    /// </summary>
    public class AutoGenerationResult
    {
        public bool Success { get; set; }
        public List<ProcessedLayout> ProcessedLayouts { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
