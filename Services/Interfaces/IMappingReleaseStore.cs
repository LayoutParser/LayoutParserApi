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
        string ETag);

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

        /// <summary>Atualiza o resultado do Fiscal Test Lab — <c>test_passed</c>/<c>test_failed</c> conforme <see cref="MappingTestRunSummary.RequiredGatesPassed"/>.</summary>
        Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken);
    }
}
