using LayoutParserApi.Services.Generation.TxtGenerator.Validators.Models;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Models
{
    public class GenerationResult
    {
        public bool Success { get; set; }
        public string FilePath { get; set; }
        public int LineCount { get; set; }
        public string ErrorMessage { get; set; }
        public FileValidationResult ValidationResult { get; set; }
    }
}
