namespace LayoutParserApi.Models.Validation
{
    /// <summary>
    /// Resultado da validação de um layout completo
    /// </summary>
    public class LayoutValidationResult
    {
        public string LayoutGuid { get; set; } = "";
        public string LayoutName { get; set; } = "";
        public bool IsValid { get; set; }
        public List<LineValidationError> Errors { get; set; } = new();
        public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;
        public int TotalLines { get; set; }
        public int ValidLines { get; set; }
        public int InvalidLines { get; set; }
    }
}