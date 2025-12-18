namespace LayoutParserApi.Models.Validation
{
    /// <summary>
    /// Resultado da validação de um documento TXT
    /// </summary>
    public class DocumentValidationResult
    {
        public bool IsValid { get; set; }
        public List<DocumentLineError> LineErrors { get; set; } = new();
        public int TotalLinesProcessed { get; set; }
        public int ValidLinesCount { get; set; }
        public int InvalidLinesCount { get; set; }
        public string ErrorMessage { get; set; } = "";
        public bool ProcessingStopped { get; set; } // Se processamento foi interrompido
    }
}
