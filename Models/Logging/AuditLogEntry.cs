namespace LayoutParserApi.Models.Logging
{
    public class AuditLogEntry : LogEntry
    {
        public new DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string UserId { get; set; }
        public new string RequestId { get; set; }
        public new string Endpoint { get; set; }
        public string Action { get; set; }
        public string Details { get; set; }
    }
}