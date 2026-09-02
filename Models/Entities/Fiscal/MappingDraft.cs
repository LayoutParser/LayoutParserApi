namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Status de uma <see cref="MappingDraftRule"/> (Slice 3 — issue #230, spec §8).
    /// <c>static class</c> em vez de <c>enum</c>, mesmo padrão de <see cref="ArtifactKind"/>.
    /// </summary>
    public static class MappingDraftRuleStatus
    {
        /// <summary>Sugerida pela IA, ainda sem decisão humana.</summary>
        public const string Proposed = "proposed";

        /// <summary>Aceita como está pelo humano.</summary>
        public const string Accepted = "accepted";

        /// <summary>Aceita com edição (sourceRefs/targetRefs/operation/etc. alterados pelo humano).</summary>
        public const string Edited = "edited";

        /// <summary>Rejeitada pelo humano — exige justificativa.</summary>
        public const string Rejected = "rejected";

        /// <summary>Evidência insuficiente — a IA nunca inventa mapping silenciosamente.</summary>
        public const string NeedsInput = "needs_input";

        /// <summary>Validada (etapa posterior, fora do escopo deste slice — reservado para o Slice 5).</summary>
        public const string Validated = "validated";

        /// <summary>Substituída por uma regra nova que cobre o mesmo TargetRefs numa rodada de sugestão posterior.</summary>
        public const string Superseded = "superseded";

        public static readonly IReadOnlyCollection<string> All = new[]
        {
            Proposed, Accepted, Edited, Rejected, NeedsInput, Validated, Superseded
        };

        public static bool IsValid(string? status) => status != null && All.Contains(status);
    }

    /// <summary>
    /// Draft de mapeamento em revisão humana (Slice 3 — issue #230). Sempre filho de uma
    /// <see cref="FiscalMappingPackageRevision"/> EXATA — nunca "a revisão mais recente" implícita.
    /// </summary>
    public class MappingDraft
    {
        public Guid DraftId { get; set; }

        public Guid WorkspaceId { get; set; }

        public Guid PackageId { get; set; }

        public Guid RevisionId { get; set; }

        /// <summary>"tcl" ou "xslt" — nunca "sysmiddle" (recusado por <c>MappingEngineGuardFilter</c>).</summary>
        public string Engine { get; set; } = string.Empty;

        public Guid CreatedByUserId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>Uma referência de evidência que sustenta uma <see cref="MappingDraftRule"/> proposta pela IA.</summary>
    public sealed record MappingDraftRuleEvidence(string Kind, string Reference);

    /// <summary>
    /// Regra de mapeamento estruturada — nunca código executável (spec §8: "A IA não grava
    /// diretamente código oficial"). Granularidade fina de concorrência otimista via
    /// <see cref="RowVersion"/> (ETag/If-Match).
    /// </summary>
    public class MappingDraftRule
    {
        public Guid RuleId { get; set; }

        public Guid DraftId { get; set; }

        public IReadOnlyList<string> SourceRefs { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> TargetRefs { get; set; } = Array.Empty<string>();

        public string Operation { get; set; } = string.Empty;

        /// <summary>JSON estruturado — livre por operação.</summary>
        public string ConditionsJson { get; set; } = "[]";

        /// <summary>JSON estruturado — livre por operação.</summary>
        public string TransformationsJson { get; set; } = "[]";

        public string Cardinality { get; set; } = "1:1";

        public IReadOnlyList<MappingDraftRuleEvidence> Evidence { get; set; } = Array.Empty<MappingDraftRuleEvidence>();

        /// <summary>"high"/"medium"/"low" — nunca inventada sem evidência (ver <see cref="MappingDraftRuleStatus.NeedsInput"/>).</summary>
        public string Confidence { get; set; } = "low";

        public string Status { get; set; } = MappingDraftRuleStatus.Proposed;

        public IReadOnlyList<string> OpenQuestions { get; set; } = Array.Empty<string>();

        public Guid CreatedByJobId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>ROWVERSION do SQL Server — ETag desta regra. 8 bytes, auto-incrementado a cada UPDATE.</summary>
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Registro append-only de cada decisão humana sobre uma <see cref="MappingDraftRule"/>
    /// (spec §8: "decisão registra ator, instante, revisão e justificativa"). Nunca sobrescrito —
    /// auditoria completa.
    /// </summary>
    public class MappingDraftRuleDecision
    {
        public Guid DecisionId { get; set; }

        public Guid RuleId { get; set; }

        public Guid ActorUserId { get; set; }

        public DateTimeOffset At { get; set; }

        /// <summary>RevisionId do pacote no momento da decisão — imutável mesmo que o pacote ganhe revisões novas depois.</summary>
        public Guid RevisionId { get; set; }

        /// <summary>Novo status decidido (accepted/edited/rejected/needs_input respondido).</summary>
        public string NewStatus { get; set; } = string.Empty;

        /// <summary>Obrigatória para rejected/edited, opcional para accepted (validado na camada de serviço).</summary>
        public string? Justification { get; set; }
    }
}
