using LayoutParserApi.Models.Logging;

namespace LayoutParserApi.Services.Interfaces
{
    public interface ILoggerService
    {
        Task LogTechnicalAsync(LogEntry entry);
        Task LogAuditAsync(AuditLogEntry entry);
        Task LogInfoAsync(string message, string endpoint, string requestId, Dictionary<string, object>? additionalData = null);
        Task LogWarningAsync(string message, string endpoint, string requestId, Dictionary<string, object>? additionalData = null);
        Task LogErrorAsync(string message, string endpoint, string requestId, Exception? exception = null, Dictionary<string, object>? additionalData = null);
    }
}
