using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Regra pronta para resposta HTTP, incluindo o ETag (base64 do ROWVERSION).</summary>
    public sealed record MappingDraftRuleDetail(
        Guid RuleId,
        Guid DraftId,
        IReadOnlyList<string> SourceRefs,
        IReadOnlyList<string> TargetRefs,
        string Operation,
        string ConditionsJson,
        string TransformationsJson,
        string Cardinality,
        IReadOnlyList<MappingDraftRuleEvidence> Evidence,
        string Confidence,
        string Status,
        IReadOnlyList<string> OpenQuestions,
        DateTimeOffset CreatedAt,
        string ETag);

    /// <summary>Draft com todas as regras atuais (não-superseded incluídas — o cliente decide o que exibir).</summary>
    public sealed record MappingDraftDetail(
        Guid DraftId,
        Guid WorkspaceId,
        Guid PackageId,
        Guid RevisionId,
        string Engine,
        DateTimeOffset CreatedAt,
        IReadOnlyList<MappingDraftRuleDetail> Rules);

    /// <summary>Resultado de um PATCH de regra — distingue NotFound (404) de Conflict (412 — ETag divergente).</summary>
    public enum UpdateRuleResult
    {
        Success,
        NotFound,
        Conflict,
    }

    public sealed record UpdateRuleOutcome(UpdateRuleResult Result, MappingDraftRuleDetail? Rule);

    /// <summary>Entrada de regra candidata gerada pelo job de sugestão — sem RuleId/RowVersion ainda (INSERT).</summary>
    public sealed record MappingDraftRuleProposal(
        IReadOnlyList<string> SourceRefs,
        IReadOnlyList<string> TargetRefs,
        string Operation,
        string ConditionsJson,
        string TransformationsJson,
        string Cardinality,
        IReadOnlyList<MappingDraftRuleEvidence> Evidence,
        string Confidence,
        string Status,
        IReadOnlyList<string> OpenQuestions);

    /// <summary>Referência a um artefato em filesystem — usado pelo job de sugestão para ler o conteúdo-fonte.</summary>
    public sealed record ArtifactFileRef(Guid ArtifactId, string Kind, string StoragePath, string OriginalFileName);

    /// <summary>
    /// Acesso a dado de <see cref="MappingDraft"/>/<see cref="MappingDraftRule"/>/
    /// <see cref="MappingDraftRuleDecision"/> (Slice 3 — issue #230). Mesmo padrão ADO.NET cru de
    /// <c>SqlFiscalPackageStore</c>, com concorrência otimista via <c>ROWVERSION</c> nativo do SQL
    /// Server na tabela de regra.
    /// </summary>
    public interface IMappingDraftStore
    {
        /// <summary>Confirma que a revisão pertence ao pacote informado (o Draft referencia uma revisão EXATA, nunca implícita).</summary>
        Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken);

        /// <summary>Lista os artefatos (com caminho de storage) da revisão — usado pelo job de sugestão para ler o conteúdo-fonte.</summary>
        Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken);

        Task<MappingDraftDetail> CreateDraftAsync(
            Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken);

        /// <summary>Draft + regras, só se <paramref name="userId"/> for membro do workspace dono (isolamento cross-workspace).</summary>
        Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken);

        /// <summary>Uma regra isolada + seu draft pai, só se membro — usado pelo PATCH.</summary>
        Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken);

        /// <summary>
        /// Insere as regras candidatas do job de sugestão. Marca como <c>superseded</c> qualquer regra
        /// já decidida (status != proposed/needs_input) que cubra o mesmo TargetRefs — nunca apaga.
        /// </summary>
        Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken);

        /// <summary>
        /// UPDATE otimista: <c>WHERE RuleId=@id AND RowVersion=@expected</c>. RowCount=0 distingue
        /// "não existe"(NotFound) de "conflito"(Conflict) via consulta de existência separada.
        /// </summary>
        Task<UpdateRuleOutcome> UpdateRuleStatusAsync(
            Guid draftId,
            Guid ruleId,
            Guid userId,
            byte[] expectedRowVersion,
            string newStatus,
            string? justification,
            IReadOnlyList<string>? editedSourceRefs,
            IReadOnlyList<string>? editedTargetRefs,
            string? editedOperation,
            CancellationToken cancellationToken);
    }
}
