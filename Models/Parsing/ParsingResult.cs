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

        /// <summary>
        /// Populado em <c>LayoutParserService.ParseTextWithSequenceValidation</c> — carrega os
        /// sinais aditivos de linha do contrato de 2026-08-27
        /// (<c>docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md</c>):
        /// <see cref="LineInfo.IsDeclaredEmpty"/> e <see cref="LineInfo.PositionalAlignmentFailed"/>.
        ///
        /// <para>⚠️ <b>Gap conhecido (confirmado em código, 2026-08-27):</b> esta lista É preenchida
        /// internamente, mas <c>ParseController.Upload</c> ainda NÃO a inclui no payload de
        /// <c>POST /api/parse/upload</c> — o objeto de resposta anônimo (linhas ~303-322 do
        /// controller) não referencia <c>result.LineInfos</c> em nenhum campo. Ou seja, hoje os dois
        /// sinais aditivos existem no back-end mas **não chegam ao front** por esse endpoint.
        /// Fechar esse gap (serializar <c>lineInfos</c> na resposta) é trabalho de
        /// <c>@lp-backend-dev</c>, ainda não agendado nesta PR (#198).</para>
        /// </summary>
        public List<LineInfo> LineInfos { get; set; } = new();

        /// <summary>
        /// Erros de validação do documento (se houver)
        /// </summary>
        public List<DocumentValidationErrorInfo> ValidationErrors { get; set; } = new();
    }
}