using LayoutParserApi.Models.Logging;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Lê e normaliza os 3 arquivos de log de texto do ecossistema (layoutparserapi.log,
    /// layoutparserlib.log, layoutparserdecrypt.log) em um formato único, paginado e filtrável,
    /// para consumo por GET api/logs.
    /// </summary>
    public interface IUnifiedLogReaderService
    {
        Task<PagedLogResult> GetLogsAsync(UnifiedLogFilter filter);
    }
}
