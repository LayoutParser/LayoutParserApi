namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Um arquivo recebido no upload, já lido em memória pelo controller (até o limite de tamanho).</summary>
    public sealed record UploadedArtifactInput(string Kind, string OriginalFileName, string ContentType, byte[] Content);

    /// <summary>Resultado de uma tentativa de criação de pacote — pode falhar por validação (422) sem lançar.</summary>
    public sealed record CreatePackageOutcome(bool Success, string? Error, PackageDetail? Package);

    /// <summary>
    /// Orquestra o upload de um <c>FiscalMappingPackage</c> (Slice 2 — issue #229): valida cada
    /// artefato (<c>MultipartUploadValidator</c>) → grava no filesystem → grava metadado no SQL →
    /// dispara scan de antivírus assíncrono. Fail-closed na validação (nenhuma inferência silenciosa),
    /// resiliente no scan (best-effort, não bloqueia).
    /// </summary>
    public interface IFiscalPackageService
    {
        /// <summary>
        /// <paramref name="idempotencyKey"/> vem do header <c>Idempotency-Key</c> quando presente;
        /// se ausente, a implementação deriva um hash determinístico do conjunto de artefatos. Reenviar
        /// a mesma chave para o mesmo (workspace, projeto) devolve o pacote já criado, sem duplicar.
        /// </summary>
        Task<CreatePackageOutcome> CreatePackageAsync(
            Guid workspaceId,
            Guid projectId,
            Guid userId,
            string packageName,
            string? idempotencyKey,
            IReadOnlyList<UploadedArtifactInput> artifacts,
            CancellationToken cancellationToken);

        Task<PackageDetail?> GetPackageIfMemberAsync(Guid packageId, Guid userId, CancellationToken cancellationToken);
    }
}
