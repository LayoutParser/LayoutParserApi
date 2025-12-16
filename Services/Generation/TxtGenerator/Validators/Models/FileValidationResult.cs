namespace LayoutParserApi.Services.Generation.TxtGenerator.Validators.Models
{
    public class FileValidationResult : ValidationResult
    {
        public int TotalLines { get; set; }
        public int ValidLines { get; set; }
        public int InvalidLines { get; set; }
    }
}
