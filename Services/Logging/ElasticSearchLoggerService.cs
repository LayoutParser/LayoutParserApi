using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using System.Text.Json;

namespace LayoutParserApi.Services.Logging
{
    public class ElasticSearchLoggerService : ILoggerService
    {
        private readonly HttpClient _httpClient;
        private readonly string _elasticSearchUrl;
        private readonly string _indexPrefix;

        public ElasticSearchLoggerService(string elasticSearchUrl, string indexPrefix = "layoutparser")
        {
            _httpClient = new HttpClient();
            _elasticSearchUrl = elasticSearchUrl.TrimEnd('/');
            _indexPrefix = indexPrefix;
        }

        public async Task LogTechnicalAsync(LogEntry entry)
        {
            var indexName = $"{_indexPrefix}-technical-{DateTime.UtcNow:yyyy.MM}";
            await IndexDocumentAsync(indexName, entry);
        }

        public async Task LogAuditAsync(AuditLogEntry entry)
        {
            var indexName = $"{_indexPrefix}-audit-{DateTime.UtcNow:yyyy.MM}";
            await IndexDocumentAsync(indexName, entry);
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

        private async Task IndexDocumentAsync<T>(string indexName, T document) where T : LogEntry
        {
            try
            {
                var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var url = $"{_elasticSearchUrl}/{indexName}/_doc";
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                    // Fallback: salvar em arquivo local se o ElasticSearch falhar
                    await SaveToFallbackFile(indexName, json);
            }
            catch (Exception ex)
            {
                // Fallback em caso de erro
                await SaveToFallbackFile(indexName, $"Error sending to ElasticSearch: {ex.Message}");
            }
        }

        private async Task SaveToFallbackFile(string indexName, string content)
        {
            var fallbackDir = "Logs/ElasticSearch_Fallback";
            if (!Directory.Exists(fallbackDir))
                Directory.CreateDirectory(fallbackDir);

            var fileName = $"{indexName}_{DateTime.UtcNow:yyyyMMdd}.log";
            var filePath = Path.Combine(fallbackDir, fileName);

            await File.AppendAllTextAsync(filePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {content}{Environment.NewLine}{Environment.NewLine}");
        }
    }
}