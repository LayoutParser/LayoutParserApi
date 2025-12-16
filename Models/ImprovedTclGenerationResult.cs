namespace LayoutParserApi.Models
{
    /// <summary>
    /// Resultado da geração melhorada de TCL
    /// </summary>
    public class ImprovedTclGenerationResult
    {

        public bool Success { get; set; }
        public string GeneratedTcl { get; set; }
        public string SuggestedTcl { get; set; }
        public List<string> Suggestions { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
