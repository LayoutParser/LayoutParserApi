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
        Guid? PreviousPublishedReleaseId,
        // Issue #379 (ADR perfil fiscal): snapshot congelado na compilação. Trailing com default —
        // não quebra call sites existentes.
        FiscalProfile? FiscalProfile = null,
        // Issue #381 (ADR edição manual de artefato §2.1): "compiled" (default) ou "manual_edit".
        // Trailing com default — não quebra call sites existentes.
        string ArtifactSource = LayoutParserApi.Models.Entities.Fiscal.MappingReleaseArtifactSource.Compiled,
        Guid? DerivedFromReleaseId = null,
        string? ManualEditReason = null,
        IReadOnlyList<string>? ManuallyEditedArtifactKinds = null)
    {
        /// <summary>
        /// Derivado, não persistido (ADR §2.2 item 2): <c>true</c> quando o artefato não é mais função
        /// das regras estruturadas — o front desabilita o diff-por-regra e mostra o selo "editado
        /// manualmente" para os kinds em <see cref="ManuallyEditedArtifactKinds"/>.
        /// </summary>
        public bool RulesDesynced => ArtifactSource == LayoutParserApi.Models.Entities.Fiscal.MappingReleaseArtifactSource.ManualEdit;
    }

    /// <summary>
    /// Resultado de <see cref="IMappingReleaseStore.CreateManualEditArtifactReleaseAsync"/> (issue #381).
    /// Distingue os três desfechos do <c>PATCH .../artifacts/{engine}</c> (ADR §4): <c>Success</c> (200,
    /// idempotente se o mesmo texto já convergiu antes), <c>NoBaseRelease</c> (não há release compilada
    /// do draft/engine para servir de base — 422) e <c>Conflict</c> (412, <c>If-Match</c> divergente).
    /// </summary>
    public enum CreateManualEditResult
    {
        Success,
        NoBaseRelease,
        Conflict,
    }

    /// <summary><see cref="CurrentArtifact"/> só é preenchido em <see cref="CreateManualEditResult.Conflict"/> (corpo do 412).</summary>
    public sealed record CreateManualEditOutcome(CreateManualEditResult Result, MappingReleaseDetail? Release, MappingReleaseArtifact? CurrentArtifact);

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
            CancellationToken cancellationToken,
            // Issue #379: snapshot do perfil fiscal do draft no momento da compilação (ADR §2.1/§2.3).
            // Trailing com default — não quebra os call sites de teste existentes.
            FiscalProfile? fiscalProfile = null);

        Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken);

        /// <summary>
        /// Lista releases do workspace, paginado e ordenado por <c>CreatedAt DESC</c> (mais recente
        /// primeiro). Isolamento por <paramref name="workspaceId"/> feito na query SQL — nunca filtra
        /// em memória (RBAC de acesso ao workspace já é responsabilidade de
        /// <c>RequireWorkspaceRoleAttribute</c> no controller).
        /// <para>
        /// Filtros opcionais (issue #377): <paramref name="status"/> (um valor de
        /// <see cref="MappingReleaseStatus"/>), <paramref name="draftId"/> e <paramref name="environment"/>.
        /// Quando <c>null</c>/vazio, o filtro não entra na cláusula <c>WHERE</c> — sem filtro o
        /// comportamento é idêntico ao anterior. Validação de valor é responsabilidade do controller.
        /// </para>
        /// </summary>
        Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(
            Guid workspaceId, int page, int pageSize,
            string? status, Guid? draftId, string? environment,
            CancellationToken cancellationToken);

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

        /// <summary>
        /// <c>published → deprecated</c> (arquivamento/deprecação manual — issue #378, cross-check #198.1).
        /// Espelha <see cref="ApproveAsync"/>/<see cref="PublishAsync"/>: valida o estado de origem,
        /// faz o <c>UPDATE</c> de status e grava a transição em <c>MappingTransition</c> na mesma operação.
        /// Idempotente (mesmo padrão do <see cref="RollbackAsync"/>): se a release já está
        /// <c>deprecated</c>, é no-op — devolve o estado atual sem gravar nova transição. Qualquer
        /// outro status de origem (não <c>published</c>) lança <see cref="InvalidOperationException"/>.
        /// </summary>
        Task<MappingReleaseDetail> DeprecateAsync(Guid releaseId, Guid actorUserId, string? justification, CancellationToken cancellationToken);

        /// <summary>
        /// <c>deprecated → archived</c> (também aceita <c>test_failed</c> e <c>in_review</c> abandonados
        /// como origem — issue #378). Congela a release em estado terminal. Grava a transição em
        /// <c>MappingTransition</c>. Idempotente: se já está <c>archived</c>, é no-op. Origem fora de
        /// <c>{deprecated, test_failed, in_review}</c> lança <see cref="InvalidOperationException"/>
        /// (uma release <c>published</c> precisa ser deprecada antes de arquivada).
        /// </summary>
        Task<MappingReleaseDetail> ArchiveAsync(Guid releaseId, Guid actorUserId, string? justification, CancellationToken cancellationToken);

        /// <summary>
        /// <c>PATCH .../mapping-drafts/{draftId}/artifacts/{engine}</c> (issue #381, ADR
        /// <c>adr-edicao-manual-artefato-versionamento-2026-09-10.md</c> §2.1/§4). Cria uma
        /// <see cref="MappingRelease"/> derivada com <c>ArtifactSource=manual_edit</c>, herdando
        /// <c>RulesSnapshotHash</c> da release-base (mais recente do <paramref name="draftId"/> para
        /// <paramref name="engine"/>) e identidade nova <c>(DraftId, RulesSnapshotHash, ArtifactContentHash)</c>.
        /// Concorrência otimista: <paramref name="expectedArtifactHash"/> precisa bater com o
        /// <see cref="MappingReleaseArtifact.Hash"/> atual da base — <see cref="CreateManualEditResult.Conflict"/>
        /// senão. Idempotente no eixo do conteúdo: mesmo texto final já produzido antes devolve a release
        /// existente, não duplica. Grava <c>MappingTransition</c> sintética <c>null → draft_compiled</c>
        /// com <c>Justification = manualEditReason</c> e <c>ChecksSnapshot</c> com
        /// <c>{ derivedFromReleaseId, manuallyEditedArtifactKinds, baseArtifactHash }</c>.
        /// </summary>
        Task<CreateManualEditOutcome> CreateManualEditArtifactReleaseAsync(
            Guid workspaceId,
            Guid draftId,
            string engine,
            string content,
            string manualEditReason,
            string expectedArtifactHash,
            Guid actorUserId,
            string correlationId,
            CancellationToken cancellationToken);
    }
}
