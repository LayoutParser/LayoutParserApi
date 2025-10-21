namespace LayoutParserApi.Models.Logging
{
    public class TechLogEntry : LogEntry
    {
        public string? Exception { get; set; }
        public string? StackTrace { get; set; }
        public string? MethodName { get; set; }
        public string? ClassName { get; set; }
    }
}
