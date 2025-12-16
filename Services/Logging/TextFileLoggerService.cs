using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using System.Text;

namespace LayoutParserApi.Services.Logging
{
    public class TextFileLoggerService : ILoggerService
    {
        private readonly string _logDirectory;
        private readonly object _lockObject = new object();

        public TextFileLoggerService(string logDirectory = "Logs")
        {
            _logDirectory = logDirectory;

            // Garante que o diretório existe
            if (!Directory.Exists(_logDirectory))
                Directory.CreateDirectory(_logDirectory);
        }

        public async Task LogTechnicalAsync(LogEntry entry)
        {
            var logMessage = FormatTechnicalLog(entry);
            await WriteToFileAsync("technical", entry.RequestId, logMessage);
        }

        public async Task LogAuditAsync(AuditLogEntry entry)
        {
            var logMessage = FormatAuditLog(entry);
            await WriteToFileAsync("audit", entry.RequestId, logMessage);
        }

        public async Task LogInfoAsync(string message, string endpoint, string requestId, Dictionary<string, object>? additionalData = null)
        {
            var entry = new LogEntry
            {
                RequestId = requestId,
                Level = "Info",
                Message = message,
                Endpoint = endpoint,
                AdditionalData = additionalData,
                Timestamp = DateTime.UtcNow
            };

            await LogTechnicalAsync(entry);
        }

        public async Task LogWarningAsync(string message, string endpoint, string requestId, Dictionary<string, object>? additionalData = null)
        {
            var entry = new LogEntry
            {
                RequestId = requestId,
                Level = "Warning",
                Message = message,
                Endpoint = endpoint,
                AdditionalData = additionalData,
                Timestamp = DateTime.UtcNow
            };

            await LogTechnicalAsync(entry);
        }

        public async Task LogErrorAsync(string message, string endpoint, string requestId, Exception? exception = null, Dictionary<string, object>? additionalData = null)
        {
            var entry = new LogEntry
            {
                RequestId = requestId,
                Level = "Error",
                Message = message,
                Endpoint = endpoint,
                StackTrace = exception?.StackTrace,
                AdditionalData = additionalData,
                Timestamp = DateTime.UtcNow
            };

            await LogTechnicalAsync(entry);
        }

        private string FormatTechnicalLog(LogEntry entry)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level.ToUpper()}]");
            sb.AppendLine($"RequestId: {entry.RequestId}");
            sb.AppendLine($"Endpoint: {entry.Endpoint}");
            sb.AppendLine($"Message: {entry.Message}");

            if (!string.IsNullOrEmpty(entry.StackTrace))
                sb.AppendLine($"StackTrace: {entry.StackTrace}");

            if (entry.AdditionalData != null && entry.AdditionalData.Any())
            {
                sb.AppendLine("Additional Data:");
                foreach (var data in entry.AdditionalData)
                    sb.AppendLine($"  {data.Key}: {data.Value}");
            }

            sb.AppendLine(new string('-', 80));
            return sb.ToString();
        }

        private string FormatAuditLog(AuditLogEntry entry)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [AUDIT]");
            sb.AppendLine($"RequestId: {entry.RequestId}");
            sb.AppendLine($"User: {entry.UserId}");
            sb.AppendLine($"Action: {entry.Action}");
            sb.AppendLine($"Details: {entry.Details}");

            sb.AppendLine(new string('-', 80));
            return sb.ToString();
        }

        private async Task WriteToFileAsync(string logType, string requestId, string message)
        {
            var fileName = $"{logType}_{DateTime.UtcNow:yyyyMMdd}.log";
            var filePath = Path.Combine(_logDirectory, fileName);

            lock (_lockObject)
            {
                File.AppendAllText(filePath, message, Encoding.UTF8);
            }

            await Task.CompletedTask;
        }
    }
}