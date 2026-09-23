using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Persistência de METADADO do histórico de análises fiscais (issue #366) no
    /// <c>IdentityDatabase:*</c>. O conteúdo dos arquivos vive em disco (ver
    /// <see cref="IFiscalAnalysisService"/>); aqui só linhas + hash. Toda leitura/remoção pelo usuário
    /// é filtrada por <c>WorkspaceId</c> E <c>OwnerUserId</c> — análise de outro membro é
    /// indistinguível de inexistente (fail-closed → 404 no controller).
    /// </summary>
    public interface IFiscalAnalysisStore
    {
        /// <summary>Insere a análise e seus arquivos numa única transação.</summary>
        Task CreateAsync(FiscalAnalysisRecord analysis, IReadOnlyList<FiscalAnalysisFileRecord> files, CancellationToken cancellationToken);

        /// <summary>Página do dono no workspace, <c>CreatedAt DESC</c>, ignorando expiradas (<c>ExpiresAt &lt;= nowUtc</c>).</summary>
        Task<FiscalAnalysisPage> ListAsync(Guid workspaceId, Guid ownerUserId, int page, int pageSize, DateTime nowUtc, CancellationToken cancellationToken);

        /// <summary>Detalhe com arquivos; <c>null</c> se não existe, é de outro dono/workspace ou expirou.</summary>
        Task<FiscalAnalysisDetail?> GetAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, DateTime nowUtc, CancellationToken cancellationToken);

        /// <summary>Remove a linha (cascade nos arquivos) só se for do dono; <c>false</c> = não encontrada.</summary>
        Task<bool> DeleteAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken cancellationToken);

        /// <summary>Análises com <c>ExpiresAt &lt;= nowUtc</c> (lote limitado) para a purga.</summary>
        Task<IReadOnlyList<FiscalAnalysisExpiredRef>> ListExpiredAsync(DateTime nowUtc, int batchSize, CancellationToken cancellationToken);

        /// <summary>Remove a linha sem checar dono (uso exclusivo da purga).</summary>
        Task DeleteByIdAsync(Guid analysisId, CancellationToken cancellationToken);

        /// <summary>Subconjunto de <paramref name="analysisIds"/> que ainda tem linha (varredura de órfãos em disco).</summary>
        Task<IReadOnlySet<Guid>> GetExistingIdsAsync(IReadOnlyCollection<Guid> analysisIds, CancellationToken cancellationToken);
    }
}
