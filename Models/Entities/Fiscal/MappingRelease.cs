namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Status de uma <see cref="MappingRelease"/> (Slice 5 — issue #231, design §2). NÃO inclui
    /// <c>approved</c>/<c>published</c> — ciclo de vida completo é do Slice 7 (governança).
    /// </summary>
    public static class MappingReleaseStatus
    {
        /// <summary>Artefato(s) gerados pelo transpilador determinístico — ainda sem teste.</summary>
        public const string DraftCompiled = "draft_compiled";

        /// <summary>Passou no Fiscal Test Lab (XSD válido + diff canônico sem divergência).</summary>
        public const string TestPassed = "test_passed";

        /// <summary>Falhou no Fiscal Test Lab — <c>RequiredGatesPassed=false</c>, bloqueia o Slice 7.</summary>
        public const string TestFailed = "test_failed";

        public static readonly IReadOnlyCollection<string> All = new[] { DraftCompiled, TestPassed, TestFailed };
    }

    /// <summary>"tcl" ou "xslt" — o conteúdo gerado pelo <c>MappingDraftRuleTranspiler</c> para o motor.</summary>
    public sealed record MappingReleaseArtifact(string Kind, string Content, string Hash, DateTimeOffset GeneratedAt);

    /// <summary>Diagnóstico de uma regra que não pôde ser transpilada (espelha <c>TranspileDiagnostic</c>, persistido).</summary>
    public sealed record MappingReleaseCompileDiagnostic(Guid RuleId, string Severity, string Message);

    /// <summary>
    /// Uma divergência encontrada pelo Fiscal Test Lab, com provenance completa: nó do XML →
    /// regra de origem (via <c>lp:ruleId</c>/atributo <c>ruleId</c> embutido pelo transpilador) →
    /// evidência da regra (Slice 3) → campo/posição de origem (<c>SourceRefs</c> da regra).
    /// </summary>
    public sealed record MappingTestRunDivergence(
        string Kind,
        string XPath,
        string? Expected,
        string? Actual,
        Guid? RuleId,
        IReadOnlyList<string>? SourceRefs,
        IReadOnlyList<MappingDraftRuleEvidence>? Evidence);

    /// <summary>Resumo do Fiscal Test Lab — <see cref="RequiredGatesPassed"/> é o contrato com o Slice 7 (design §2).</summary>
    public sealed record MappingTestRunSummary(
        int Passed,
        int Failed,
        double CoveragePercent,
        bool RequiredGatesPassed,
        bool XsdValid,
        IReadOnlyList<string> XsdErrors,
        IReadOnlyList<MappingTestRunDivergence> Divergences);

    /// <summary>
    /// Artefato compilado + resultado de teste de um <see cref="MappingDraft"/> (Slice 5 — issue #231).
    /// Nasce em <see cref="MappingReleaseStatus.DraftCompiled"/> na compilação, evolui para
    /// <c>test_passed</c>/<c>test_failed</c> após o Fiscal Test Lab. Nunca <c>approved</c>/<c>published</c>
    /// nesta etapa (Slice 7).
    /// </summary>
    public class MappingRelease
    {
        public Guid ReleaseId { get; set; }

        public Guid WorkspaceId { get; set; }

        public Guid DraftId { get; set; }

        /// <summary>"tcl" ou "xslt" — nunca "sysmiddle", herdado do draft.</summary>
        public string Engine { get; set; } = string.Empty;

        public IReadOnlyList<MappingReleaseArtifact> Artifacts { get; set; } = Array.Empty<MappingReleaseArtifact>();

        /// <summary>Snapshot dos RuleId accepted/edited no momento da compilação — proveniência até a decisão humana.</summary>
        public IReadOnlyList<Guid> SourceRuleIds { get; set; } = Array.Empty<Guid>();

        public IReadOnlyList<MappingReleaseCompileDiagnostic> CompileDiagnostics { get; set; } = Array.Empty<MappingReleaseCompileDiagnostic>();

        /// <summary>Hash do conjunto de regras accepted/edited (idempotência: mesmo draft + mesmo snapshot não duplica).</summary>
        public string RulesSnapshotHash { get; set; } = string.Empty;

        public MappingTestRunSummary? TestRunSummary { get; set; }

        public string Status { get; set; } = MappingReleaseStatus.DraftCompiled;

        public string CorrelationId { get; set; } = string.Empty;

        public Guid CreatedByJobId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }
}
