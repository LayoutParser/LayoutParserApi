using LayoutParserApi.Models.Enums;

using System.Text;
using System.Xml;

namespace LayoutParserApi.Models.Parsing
{
    /// <summary>
    /// Classifica a falha do parse por TIPO DE EXCEÇÃO e traduz a causa para o contrato HTTP
    /// (código de wire + status). Resolver PURO (sem I/O e sem logging), no mesmo espírito de
    /// <see cref="Configuration.LineLengthResolver"/> e
    /// <see cref="Configuration.PositionalFormatResolver"/> — quem tem <c>ILogger</c> loga.
    ///
    /// <para>Ver <c>docs/architecture/spec-taxonomia-de-falha-do-parse.md</c> §3. Os literais de
    /// <c>*Code</c> são CONTRATO com o front-end: renomeá-los quebra o outro lado.</para>
    /// </summary>
    public static class ParseFailure
    {
        // ── Códigos de wire (contrato com o front — não renomear sem avisar o outro lado) ────────
        public const string DocumentMalformedCode = "document_malformed";
        public const string LayoutInvalidCode = "layout_invalid";
        public const string ParserDefectCode = "parser_defect";

        /// <summary>
        /// Mensagem devolvida ao cliente quando a culpa é nossa. FIXA e sem detalhe de exceção:
        /// stack trace e mensagem interna vazam estrutura do parser e não ajudam o usuário —
        /// o detalhe vai para o log estruturado, correlacionado pelo <c>correlationId</c>.
        /// </summary>
        public const string ParserDefectSafeMessage = "Falha interna ao processar o documento.";

        /// <summary>
        /// Mensagem para o caso "documento sem conteúdo": não é defeito localizável (não há linha
        /// a anotar), é ausência de documento — irrecuperável, sem nada a renderizar.
        /// </summary>
        public const string EmptyDocumentMessage = "O documento enviado está vazio — não há conteúdo para parsear.";

        /// <summary>Fallback quando a falha é de entrada mas o parser não registrou motivo.</summary>
        public const string GenericClientMessage = "Não foi possível parsear o documento com o layout informado.";

        /// <summary>
        /// Classificação por tipo de exceção (spec §3).
        ///
        /// <para><b>Divisão por ARTEFATO,</b> que é o que o usuário consegue agir sobre:
        /// <list type="bullet">
        /// <item><see cref="XmlException"/> — o único XML lido neste fluxo é o do LAYOUT
        /// (o documento posicional é texto puro). Layout ilegível ⇒
        /// <see cref="ParseFailureCause.LayoutInvalid"/>, e o front aponta o layout.</item>
        /// <item><see cref="DecoderFallbackException"/> — encoding do DOCUMENTO ⇒
        /// <see cref="ParseFailureCause.DocumentMalformed"/>, e o front aponta o arquivo de dados.</item>
        /// <item><b>Qualquer outra</b> ⇒ <see cref="ParseFailureCause.ParserDefect"/>. Inclusive
        /// <c>Exception</c> crua: um tipo genérico é indistinguível de falha aleatória, então cai
        /// no default que culpa a nós.</item>
        /// </list></para>
        /// </summary>
        /// <param name="excecao">Exceção que abortou o parse. <c>null</c> ⇒ falha sem exceção
        /// catalogada, que também é defeito nosso.</param>
        public static ParseFailureCause Classify(Exception? excecao) => excecao switch
        {
            XmlException => ParseFailureCause.LayoutInvalid,
            DecoderFallbackException => ParseFailureCause.DocumentMalformed,
            _ => ParseFailureCause.ParserDefect
        };

        /// <summary>Código emitido no campo <c>failureCause</c> do payload de erro.</summary>
        public static string ToWireCode(ParseFailureCause causa) => causa switch
        {
            ParseFailureCause.DocumentMalformed => DocumentMalformedCode,
            ParseFailureCause.LayoutInvalid => LayoutInvalidCode,
            _ => ParserDefectCode
        };

        /// <summary>
        /// <c>422</c> quando a entrada é ruim (não há documento a mostrar); <c>500</c> quando o
        /// defeito é nosso.
        /// </summary>
        public static int ToHttpStatusCode(ParseFailureCause causa) =>
            causa == ParseFailureCause.ParserDefect
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status422UnprocessableEntity;

        /// <summary>
        /// Mensagem segura para o cliente: em <see cref="ParseFailureCause.ParserDefect"/> ignora
        /// o motivo interno (que carrega texto de exceção) e devolve o literal fixo.
        /// </summary>
        public static string ResolveClientMessage(ParseFailureCause causa, string? motivoInterno)
        {
            if (causa == ParseFailureCause.ParserDefect)
                return ParserDefectSafeMessage;

            return string.IsNullOrWhiteSpace(motivoInterno) ? GenericClientMessage : motivoInterno;
        }
    }
}
