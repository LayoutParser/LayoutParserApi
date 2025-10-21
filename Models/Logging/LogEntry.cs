using LayoutParserApi.Models.Logging.Interface;

namespace LayoutParserApi.Models.Logging
{
    public class LogEntry : ILogEntry
    {
        public string RequestId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Level { get; set; } = "Info";
        public string Message { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public Dictionary<string, object>? AdditionalData { get; set; }
        public string StackTrace { get; internal set; }

        public Exception? Exception { get; set; }
    }
}
