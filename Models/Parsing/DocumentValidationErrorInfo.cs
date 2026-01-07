namespace LayoutParserApi.Models.Parsing
{
    /// <summary>
    /// Informação de erro de validação do documento (versão simplificada para ParsingResult)
    /// </summary>
    public class DocumentValidationErrorInfo
    {
        public int LineIndex { get; set; }
        public string Sequence { get; set; } = "";
        public int ExpectedLength { get; set; }
        public int ActualLength { get; set; }
        public string ErrorMessage { get; set; } = "";
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }
    }
}
