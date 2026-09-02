namespace LayoutParserApi.Models.Dtos.Fiscal
{
    /// <summary>
    /// Níveis de confiabilidade de uma <see cref="ExplainedRule"/> — nunca inventa "authoritative"
    /// sem gramática/AST reconhecida por trás (Slice 4 — issue #226/#227, design §1).
    /// </summary>
    public static class MappingExplanationSupportLevel
    {
        /// <summary>Regra reconhecida 100% pela gramática/AST conhecida.</summary>
        public const string Authoritative = "authoritative";

        /// <summary>Reconhecida mas com heurística/ainda não revisada por humano.</summary>
        public const string BestEffort = "best_effort";

        /// <summary>Elemento reconhecido como "existe" mas sem semântica traduzível.</summary>
        public const string Opaque = "opaque";

        /// <summary>Elemento fora de qualquer gramática esperada, ou artefato inexistente.</summary>
        public const string Unsupported = "unsupported";
    }

    /// <summary>Capacidades do motor por trás de uma explicação (Slice 4, design §1). Nunca lidas de config — cada adapter hard-coda as suas.</summary>
    public sealed record EngineCapabilities(bool Execute, bool Explain, bool Author, bool Compile, bool Publish);

    /// <summary>Referência de schema de origem/destino (quando resolvível).</summary>
    public sealed record SchemaRef(string? LayoutGuid, string? Description);

    /// <summary>Evidência que sustenta uma <see cref="ExplainedRule"/> (mesmo shape de <c>MappingDraftRuleEvidence</c>).</summary>
    public sealed record EvidenceRef(string Kind, string Reference);

    /// <summary>Uma regra de mapeamento já traduzida para o contrato canônico, PT-BR, determinístico (sem LLM).</summary>
    public sealed record ExplainedRule(
        string RuleId,
        IReadOnlyList<string> SourceRefs,
        IReadOnlyList<string> TargetRefs,
        string? Condition,
        IReadOnlyList<string> Operations,
        string Cardinality,
        IReadOnlyList<EvidenceRef> Evidence,
        string HumanDescription,
        string? TechnicalDetail,
        string SupportLevel);

    /// <summary>
    /// Contrato canônico de explicação de mapeamento (Slice 4 — issue #226/#227), independente do
    /// motor (<c>engine=sysmiddle|tcl|xslt</c>). <c>GET .../explanation</c> sempre retorna 200 com
    /// este contrato, mesmo 100% <c>opaque</c> — nunca falha por não entender uma regra.
    /// </summary>
    public sealed record MappingExplanation(
        string MappingId,
        string Version,
        string Engine,
        EngineCapabilities Capabilities,
        SchemaRef? SourceSchema,
        SchemaRef? TargetSchema,
        IReadOnlyList<ExplainedRule> Rules,
        string? Description,
        IReadOnlyList<string> Limitations,
        int OpaqueRuleCount);
}
