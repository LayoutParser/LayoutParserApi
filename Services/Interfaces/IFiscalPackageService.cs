namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Um arquivo recebido no upload, já lido em memória pelo controller (até o limite de tamanho).</summary>
    public sealed record UploadedArtifactInput(string Kind, string OriginalFileName, string ContentType, byte[] Content);

    /// <summary>Resultado de uma tentativa de criação de pacote — pode falhar por validação (422) sem lançar.</summary>
    public sealed record CreatePackageOutcome(bool Success, string? Error, PackageDetail? Package);

    /// <summary>
    /// Resultado de uma tentativa de criar uma nova revisão. <see cref="NotFound"/> distingue "pacote
    /// não existe/não é seu" (404) de "artefatos inválidos" (422) — o controller decide o status HTTP.
    /// </summary>
    public sealed record CreateRevisionOutcome(bool Success, string? Error, bool NotFound, PackageDetail? Package);

    /// <summary>Inventário de uma aba do Excel reconhecida como tabela de decisão fiscal.</summary>
    public sealed record ExcelSheetInventory(string SheetName, IReadOnlyList<string> Columns, int RuleCount);

    /// <summary>
    /// Inventário de estrutura de um artefato <c>spec</c> (XLSX), reaproveitando
    /// <see cref="LayoutParserApi.Services.Fiscal.IFiscalMappingRuleExtractor"/> (issue #103) —
    /// nenhum parser de Excel novo. Front-end usa isto para exibir abas/colunas/linhas antes de
    /// confirmar o upload, sem que a API precise devolver o conteúdo bruto da planilha.
    /// </summary>
    public sealed record ExcelInventoryResult(IReadOnlyList<ExcelSheetInventory> DecisionSheets, IReadOnlyList<string> SkippedSheets);

    /// <summary>
    /// Resultado de uma tentativa de gerar o inventário. <see cref="NotFound"/> cobre pacote/artefato
    /// inexistente ou alheio (404); <see cref="Error"/> cobre "não é um artefato spec" ou Excel
    /// corrompido (422) — nunca propaga exceção de parsing para o cliente.
    /// </summary>
    public sealed record ExcelInventoryOutcome(bool Success, string? Error, bool NotFound, ExcelInventoryResult? Inventory);

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

        /// <summary>Lista os projetos fiscais do workspace, só para quem é membro. Leitura pura (issue de contrato #201).</summary>
        Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken);

        /// <summary>
        /// Cria uma nova revisão de um pacote já existente — mesma validação/armazenamento/antivírus
        /// assíncrono de <see cref="CreatePackageAsync"/>, mas sem criar pacote novo nem checar
        /// idempotência (reenvio idêntico intencional ainda cria uma revisão nova — é uma correção,
        /// não um reenvio de rede).
        /// </summary>
        Task<CreateRevisionOutcome> CreateRevisionAsync(
            Guid workspaceId,
            Guid packageId,
            Guid userId,
            IReadOnlyList<UploadedArtifactInput> artifacts,
            CancellationToken cancellationToken);

        /// <summary>Inventário de estrutura (abas/colunas/linhas) de um artefato <c>spec</c> (XLSX) da revisão mais recente.</summary>
        Task<ExcelInventoryOutcome> GetExcelInventoryAsync(
            Guid workspaceId,
            Guid packageId,
            Guid artifactId,
            Guid userId,
            CancellationToken cancellationToken);
    }
}
