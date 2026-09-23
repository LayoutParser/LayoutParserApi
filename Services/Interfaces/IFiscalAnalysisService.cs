using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Resultado da abertura de um arquivo do histórico.</summary>
    public enum FiscalAnalysisFileStatus { Ok, NotFound, IntegrityFailure }

    public sealed record FiscalAnalysisFileContent(FiscalAnalysisFileStatus Status, string? FileName, byte[]? Content);

    /// <summary>
    /// Histórico de análises fiscais (issue #366): grava os arquivos anexados numa análise (disco +
    /// SQL) e serve leitura/download/exclusão isolados por dono. Falha de persistência NUNCA pode
    /// derrubar o parse — <see cref="RegisterAsync"/> devolve <c>null</c> em vez de lançar.
    /// </summary>
    public interface IFiscalAnalysisService
    {
        /// <summary>
        /// Confere membership no workspace, grava arquivos em disco (sha256) e metadado no SQL.
        /// Devolve o <c>AnalysisId</c>, ou <c>null</c> se não for membro / qualquer falha / timeout
        /// (nunca lança para o chamador).
        /// </summary>
        Task<Guid?> RegisterAsync(FiscalAnalysisRegistration registration, TimeSpan timeout);

        Task<FiscalAnalysisPage> ListAsync(Guid workspaceId, Guid ownerUserId, int page, int pageSize, CancellationToken cancellationToken);

        Task<FiscalAnalysisDetail?> GetAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken cancellationToken);

        /// <summary>Lê o arquivo do disco e confere o sha256 antes de devolver; divergência → <see cref="FiscalAnalysisFileStatus.IntegrityFailure"/>.</summary>
        Task<FiscalAnalysisFileContent> OpenFileAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, Guid fileId, CancellationToken cancellationToken);

        /// <summary>Apaga linha + arquivos do dono; <c>false</c> = não encontrada/de outro dono.</summary>
        Task<bool> DeleteAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken cancellationToken);

        /// <summary>Purga análises expiradas (linha + disco) e varre diretórios órfãos. Devolve quantas análises apagou.</summary>
        Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
    }
}
