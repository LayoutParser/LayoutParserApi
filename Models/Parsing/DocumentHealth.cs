namespace LayoutParserApi.Models.Parsing
{
    /// <summary>
    /// Saúde do documento no <c>200</c>: separa "parseou limpo" de "parseou COM defeito
    /// localizável". É derivável de <c>validationErrors</c>, mas vai explícito no payload porque a
    /// UI decide o modo de exibição (documento limpo × documento anotado) antes de olhar a lista.
    ///
    /// <para>Defeito localizável NÃO é <c>422</c>: é entidade processável com problema, e o
    /// usuário precisa ver o documento com o erro anotado. O <c>422</c> fica só para o
    /// irrecuperável. Ver <c>docs/architecture/spec-taxonomia-de-falha-do-parse.md</c> §2.1.</para>
    /// </summary>
    public static class DocumentHealth
    {
        // Códigos de wire (contrato com o front — não renomear sem avisar o outro lado).
        public const string Clean = "clean";
        public const string HasDefects = "has_defects";

        /// <summary>
        /// <see cref="HasDefects"/> quando há pelo menos um erro de validação anotado;
        /// <see cref="Clean"/> caso contrário (inclusive lista nula — ausência de erro é limpo).
        /// </summary>
        public static string Resolve(IReadOnlyCollection<DocumentValidationErrorInfo>? erros) =>
            erros is { Count: > 0 } ? HasDefects : Clean;
    }
}
