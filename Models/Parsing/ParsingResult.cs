using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;
using LayoutParserApi.Models.Summaries;

namespace LayoutParserApi.Models.Parsing
{
    public class ParsingResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>
        /// De quem é a culpa quando <see cref="Success"/> é <c>false</c> — classificado pelo TIPO
        /// da exceção que abortou o parse (<see cref="ParseFailure.Classify"/>).
        ///
        /// <para><c>null</c> em caso de sucesso. <c>null</c> COM <see cref="Success"/> falso
        /// significa falha sem exceção catalogada: quem consome deve assumir
        /// <see cref="ParseFailureCause.ParserDefect"/> — o default é culpar a nós.</para>
        ///
        /// <para>A exceção em si NÃO é carregada aqui de propósito: este objeto atravessa a borda
        /// HTTP e stack trace não pode vazar no payload. O detalhe é logado onde é capturado.</para>
        /// </summary>
        public ParseFailureCause? FailureCause { get; set; }
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
}