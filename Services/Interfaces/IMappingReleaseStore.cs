using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Release pronta para resposta HTTP, incluindo o ETag (base64 do ROWVERSION).</summary>
    public sealed record MappingReleaseDetail(
        Guid ReleaseId,
        Guid WorkspaceId,
        Guid DraftId,
        string Engine,
        IReadOnlyList<MappingReleaseArtifact> Artifacts,
        IReadOnlyList<Guid> SourceRuleIds,
        IReadOnlyList<MappingReleaseCompileDiagnostic> CompileDiagnostics,
        string RulesSnapshotHash,
        MappingTestRunSummary? TestRunSummary,
        string Status,
        string CorrelationId,
        DateTimeOffset CreatedAt,
        string ETag,
        string Environment,
        Guid? ApprovedByUserId,
        DateTimeOffset? ApprovedAt,
        string? ApprovalJustification,
        Guid? PublishedByUserId,
        DateTimeOffset? PublishedAt,
        Guid? PreviousPublishedReleaseId);

    /// <summary>Uma transição de estado registrada (Slice 7) — retorno de leitura, nunca escrita direta pelo controller.</summary>
    public sealed record MappingTransitionDetail(
        Guid TransitionId,
        Guid ReleaseId,
        string FromStatus,
        string ToStatus,
        Guid ActorUserId,
        DateTimeOffset OccurredAt,
        string? Justification);

    /// <summary>
    /// Acesso a dado de <see cref="MappingRelease"/> (Slice 5 — issue #231). Mesmo padrão ADO.NET cru
    /// de <c>SqlMappingDraftStore</c>.
    /// </summary>
    public interface IMappingReleaseStore
    {
        /// <summary>
        /// Idempotente por (DraftId, RulesSnapshotHash): reenviar a mesma compilação (mesmo conjunto
        /// accepted/edited) devolve a release já existente, não duplica (design §2).
        /// </summary>
        Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(
            Guid workspaceId,
            Guid draftId,
            string engine,
            string rulesSnapshotHash,
            IReadOnlyList<Guid> sourceRuleIds,
            IReadOnlyList<MappingReleaseArtifact> artifacts,
            IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics,
            string correlationId,
            Guid jobId,
            CancellationToken cancellationToken);

        Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken);

        /// <summary>
        /// Lista releases do workspace, paginado e ordenado por <c>CreatedAt DESC</c> (mais recente
        /// primeiro). Isolamento por <paramref name="workspaceId"/> feito na query SQL — nunca filtra
        /// em memória (RBAC de acesso ao workspace já é responsabilidade de
        /// <c>RequireWorkspaceRoleAttribute</c> no controller).
        /// </summary>
        Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(
            Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken);

        /// <summary>Atualiza o resultado do Fiscal Test Lab — <c>test_passed</c>/<c>test_failed</c> conforme <see cref="MappingTestRunSummary.RequiredGatesPassed"/>.</summary>
        Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken);

        /// <summary>
        /// <c>test_passed → in_review → approved</c> (Slice 7, design §1/§4). Lança
        /// <see cref="InvalidOperationException"/> se o status atual não for <c>test_passed</c> — bloqueia
        /// <c>test_failed</c> (e qualquer outro estado) de entrar em revisão. Grava as DUAS transições em
        /// <c>MappingTransition</c> na mesma operação.
        /// </summary>
        Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken);

        /// <summary>
        /// <c>approved → published</c>. Grava <c>PreviousPublishedReleaseId</c> a partir da release que
        /// hoje está <c>published</c> para o mesmo <c>DraftId</c> (se houver) e a rebaixa para
        /// <c>deprecated</c>. Lança <see cref="InvalidOperationException"/> se o status atual não for
        /// <c>approved</c>.
        /// </summary>
        Task<MappingReleaseDetail> PublishAsync(Guid releaseId, Guid actorUserId, string environment, CancellationToken cancellationToken);

        /// <summary>
        /// Reverte a release <c>published</c> apontada por <paramref name="releaseId"/> para
        /// <c>deprecated</c> e promove <c>PreviousPublishedReleaseId</c> de volta a <c>published</c>.
        /// Idempotente (design §3): se <paramref name="releaseId"/> já não está <c>published</c>, é
        /// no-op — devolve o estado atual sem gravar nova transição.
        /// </summary>
        Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken);
    }
}
