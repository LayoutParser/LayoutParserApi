using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Inventário de artefatos de uma revisão, pronto para resposta HTTP.</summary>
    public sealed record ArtifactSummary(
        Guid ArtifactId,
        string Kind,
        string Sha256,
        long SizeBytes,
        string OriginalFileName,
        string InspectionStatus,
        DateTimeOffset UploadedAt);

    /// <summary>Resumo de uma revisão com seu inventário de artefatos.</summary>
    public sealed record RevisionSummary(
        Guid RevisionId,
        int RevisionNumber,
        DateTimeOffset CreatedAt,
        IReadOnlyList<ArtifactSummary> Artifacts);

    /// <summary>Pacote completo — só a revisão mais recente é exposta neste slice (GET §Escopo).</summary>
    public sealed record PackageDetail(
        Guid PackageId,
        Guid WorkspaceId,
        Guid ProjectId,
        string Name,
        DateTimeOffset CreatedAt,
        RevisionSummary LatestRevision);

    /// <summary>
    /// Acesso a dado de <see cref="FiscalMappingPackage"/>/<see cref="FiscalMappingPackageRevision"/>/
    /// <see cref="PackageArtifact"/> (Slice 2 — issue #229). Mesmo padrão ADO.NET cru de
    /// <c>SqlIdentityWorkspaceStore</c>: DDL idempotente na primeira chamada por processo.
    /// </summary>
    public interface IFiscalPackageStore
    {
        /// <summary>Garante o <see cref="FiscalProject"/> mínimo, criando-o se não existir (idempotente por WorkspaceId+ProjectId).</summary>
        Task<bool> EnsureProjectExistsAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken);

        /// <summary>
        /// Cria um pacote novo com sua primeira revisão e os artefatos informados. Idempotência por
        /// conteúdo é responsabilidade do chamador (verificar hash antes de decidir criar).
        /// </summary>
        Task<PackageDetail> CreatePackageAsync(
            Guid workspaceId,
            Guid projectId,
            Guid createdByUserId,
            string packageName,
            string idempotencyKey,
            IReadOnlyList<PackageArtifact> artifacts,
            CancellationToken cancellationToken);

        /// <summary>Pacote + revisão mais recente, só se <paramref name="userId"/> for membro do workspace dono.</summary>
        Task<PackageDetail?> GetPackageIfMemberAsync(Guid packageId, Guid userId, CancellationToken cancellationToken);

        /// <summary>
        /// Busca um pacote já criado com a mesma chave de idempotência (header explícito, ou hash do
        /// conjunto de artefatos quando ausente) dentro do mesmo (workspace, projeto) — reenviar o
        /// mesmo upload não deve criar duplicata.
        /// </summary>
        Task<PackageDetail?> FindPackageByIdempotencyKeyAsync(Guid workspaceId, Guid projectId, string idempotencyKey, CancellationToken cancellationToken);

        /// <summary>
        /// Busca um artefato existente pelo par (packageId, hash) — chave de idempotência de upload
        /// (mesmo conteúdo para o mesmo pacote não deve criar duplicata).
        /// </summary>
        Task<ArtifactSummary?> FindArtifactByHashAsync(Guid packageId, string sha256, CancellationToken cancellationToken);

        /// <summary>Atualiza o status de inspeção de antivírus de um artefato (job assíncrono pós-upload).</summary>
        Task UpdateInspectionStatusAsync(Guid artifactId, string inspectionStatus, CancellationToken cancellationToken);
    }
}
