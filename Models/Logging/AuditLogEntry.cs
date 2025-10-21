namespace LayoutParserApi.Models.Logging
{
    public class AuditLogEntry : LogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string UserId { get; set; }
        public string RequestId { get; set; }
        public string Endpoint { get; set; }
        public string Action { get; set; }
        public string Details { get; set; }
    }
}
