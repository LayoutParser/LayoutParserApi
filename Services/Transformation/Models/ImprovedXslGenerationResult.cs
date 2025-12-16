namespace LayoutParserApi.Services.Transformation.Models
{
    /// <summary>
    /// Resultado da geração melhorada de XSL
    /// </summary>
    public class ImprovedXslGenerationResult
    {
        public bool Success { get; set; }
        public string GeneratedXsl { get; set; }
        public string SuggestedXsl { get; set; }
        public List<string> Suggestions { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
