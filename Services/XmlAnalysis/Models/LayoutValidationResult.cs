namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    public class LayoutValidationResult
    {
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
