namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Status de uma <see cref="MappingRelease"/> (Slice 5 — issue #231, design §2 — estendido pelo
    /// Slice 7, issue #94, governança/publicação). <c>InReview</c>/<c>Approved</c>/<c>Published</c>/
    /// <c>Deprecated</c>/<c>Archived</c> são novos deste slice; não reabrem o enum, só o estendem.
    /// </summary>
    public static class MappingReleaseStatus
    {
        /// <summary>Artefato(s) gerados pelo transpilador determinístico — ainda sem teste.</summary>
        public const string DraftCompiled = "draft_compiled";

        /// <summary>Passou no Fiscal Test Lab (XSD válido + diff canônico sem divergência).</summary>
        public const string TestPassed = "test_passed";

        /// <summary>Falhou no Fiscal Test Lab — <c>RequiredGatesPassed=false</c>, bloqueia entrada em <see cref="InReview"/>.</summary>
        public const string TestFailed = "test_failed";

        /// <summary>Em revisão humana — entrada automática pelo endpoint <c>approve</c>, nunca persiste isolada.</summary>
        public const string InReview = "in_review";

        /// <summary>Aprovada por <c>Reviewer</c>/<c>FiscalAdmin</c> — pronta para publicação.</summary>
        public const string Approved = "approved";

        /// <summary>Publicada e imutável — nenhuma escrita de artefato depois disso; edição gera nova revisão.</summary>
        public const string Published = "published";

        /// <summary>Substituída por outra publicação (publish de outra release ou alvo de rollback).</summary>
        public const string Deprecated = "deprecated";

        /// <summary>Fora de uso definitivamente — reservado para retenção/expurgo futuro (não usado ainda pelos endpoints deste slice).</summary>
        public const string Archived = "archived";

        public static readonly IReadOnlyCollection<string> All = new[]
        {
            DraftCompiled, TestPassed, TestFailed, InReview, Approved, Published, Deprecated, Archived
        };
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

        /// <summary>Ambiente onde a release está/esteve ativa (Slice 7) — não onde foi testada.</summary>
        public string Environment { get; set; } = "development";

        public Guid? ApprovedByUserId { get; set; }

        public DateTimeOffset? ApprovedAt { get; set; }

        public string? ApprovalJustification { get; set; }

        public Guid? PublishedByUserId { get; set; }

        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>Snapshot gravado no momento do <c>publish</c>: a release que estava <c>Published</c> antes desta (design §3, rollback).</summary>
        public Guid? PreviousPublishedReleaseId { get; set; }

        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Registro imutável de cada transição de estado de uma <see cref="MappingRelease"/> (Slice 7 —
    /// issue #94). Uma linha por transição — <c>Justification</c> obrigatória em
    /// <c>approve</c>/<c>publish</c>/<c>rollback</c>. Não reaproveita nenhum log genérico existente:
    /// é o contrato de auditoria específico de governança citado na spec §12/§14.
    /// </summary>
    public class MappingTransition
    {
        public Guid TransitionId { get; set; }

        public Guid ReleaseId { get; set; }

        public string FromStatus { get; set; } = string.Empty;

        public string ToStatus { get; set; } = string.Empty;

        public Guid ActorUserId { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public string? Justification { get; set; }

        /// <summary>JSON livre com o snapshot dos gates que passaram no momento da transição (ex.: <see cref="MappingTestRunSummary"/>).</summary>
        public string? ChecksSnapshot { get; set; }
    }
}
