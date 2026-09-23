namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Perfil fiscal do alvo de mapeamento (issue #379 — ADR
    /// <c>docs/architecture/adr-perfil-fiscal-draft-release-2026-09-10.md</c>). Value object — vive
    /// inline no <see cref="MappingDraft"/> (cópia de trabalho, mutável) e na <see cref="MappingRelease"/>
    /// (snapshot congelado na compilação, imutável). <c>DocumentType</c>+<c>SchemaVersion</c> resolvem
    /// o XSD alvo via <c>XsdValidation:DocumentTypes</c> (pré-requisito da #380/#198.5).
    /// </summary>
    public sealed record FiscalProfile(
        string DocumentType,      // enum fechado — chave em XsdValidation:DocumentTypes
        string SchemaVersion,     // string livre — deve casar com XsdVersion da entrada acima
        string Operation,         // enum fechado — FiscalOperation.*
        string Jurisdiction);     // enum semiaberto — FiscalJurisdiction.* (UF ou "BR")

    /// <summary>documentType — fechado, espelha as chaves (normalizadas) de XsdValidation:DocumentTypes.</summary>
    public static class FiscalDocumentType
    {
        public const string Nfe = "NFe";
        public const string Cte = "CTe";
        public const string NfCom = "NFCom";
        public const string Mdfe = "MDFe";
        public static readonly IReadOnlyCollection<string> All = new[] { Nfe, Cte, NfCom, Mdfe };
        public static bool IsValid(string? v) => v != null && All.Contains(v);
    }

    /// <summary>operation fiscal — fechado. NÃO confundir com <see cref="MappingDraftRule.Operation"/> (operação de transformação da regra).</summary>
    public static class FiscalOperation
    {
        public const string Outbound = "outbound";        // emissão / saída
        public const string Inbound = "inbound";           // recebimento / entrada
        public const string Return = "return";             // devolução
        public const string Cancellation = "cancellation";
        public const string Complementary = "complementary";
        public static readonly IReadOnlyCollection<string> All =
            new[] { Outbound, Inbound, Return, Cancellation, Complementary };
        public static bool IsValid(string? v) => v != null && All.Contains(v);
    }

    /// <summary>
    /// jurisdiction — semiaberto: as 27 UFs + "BR" (federal / nacional). Validação por lista fechada
    /// de UF, mas o conjunto é notório e estável (não é string livre).
    /// </summary>
    public static class FiscalJurisdiction
    {
        public const string Federal = "BR";
        public static readonly IReadOnlyCollection<string> Ufs = new[]
        { "AC","AL","AP","AM","BA","CE","DF","ES","GO","MA","MT","MS","MG","PA","PB",
          "PR","PE","PI","RJ","RN","RS","RO","RR","SC","SP","SE","TO" };
        public static bool IsValid(string? v) => v == Federal || (v != null && Ufs.Contains(v));
    }

    /// <summary>Eco (derivado, não persistido) de uma entrada de <c>XsdValidation:DocumentTypes</c> — conveniência de resposta HTTP (issue #379, ADR §2.6).</summary>
    public sealed record FiscalResolvedXsd(string XsdVersion, string Namespace, string RootElement);
}
