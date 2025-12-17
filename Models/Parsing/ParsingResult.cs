using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Summaries;
using LayoutParserApi.Models.Validation;

namespace LayoutParserApi.Models.Parsing
{
    public class ParsingResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Layout Layout { get; set; }
        public List<ParsedField> ParsedFields { get; set; }
        public string RawText { get; set; }        
        public DocumentSummary Summary { get; set; }

        public List<string> DetectedLines { get; set; } = new();
        public List<LineInfo> LineInfos { get; set; } = new();
        
        /// <summary>
        /// Erros de validação do documento (se houver)
        /// </summary>
        public List<DocumentValidationErrorInfo> ValidationErrors { get; set; } = new();
    }

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
