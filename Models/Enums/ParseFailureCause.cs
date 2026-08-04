namespace LayoutParserApi.Models.Enums
{
    /// <summary>
    /// De quem é a culpa quando o parse não produz documento.
    ///
    /// <para>Antes desta taxonomia havia dois desfechos e três realidades: bug nosso e arquivo
    /// quebrado viravam o MESMO <c>422</c> com uma string, indistinguíveis um do outro. Isso culpa
    /// o arquivo do usuário mesmo quando a culpa é nossa, e apaga o sinal de que temos um defeito.</para>
    ///
    /// <para><b>Regra não-negociável:</b> o default é culpar a NÓS. Exceção não catalogada é
    /// <see cref="ParserDefect"/> até prova em contrário — dizer "seu arquivo está errado" quando
    /// não sabemos é pior que um 500 honesto: manda o usuário caçar problema em arquivo bom.</para>
    ///
    /// Ver <c>docs/architecture/spec-taxonomia-de-falha-do-parse.md</c> §3.
    /// </summary>
    public enum ParseFailureCause
    {
        /// <summary>
        /// Problema do lado do DOCUMENTO: conteúdo ilegível ou inexistente (documento vazio,
        /// encoding inválido). O front aponta o usuário para o arquivo de dados. HTTP 422.
        /// </summary>
        DocumentMalformed,

        /// <summary>
        /// Problema do lado do LAYOUT: o XML do layout não pôde sequer ser lido, ou não descreve o
        /// documento enviado. O front aponta o usuário para o layout selecionado. HTTP 422.
        /// </summary>
        LayoutMismatch,

        /// <summary>
        /// Defeito NOSSO: exceção não catalogada como entrada ruim (<c>NullReferenceException</c>,
        /// <c>IndexOutOfRangeException</c>, etc.). HTTP 500 com mensagem segura — o detalhe fica
        /// só no log estruturado.
        /// </summary>
        ParserDefect
    }
}
