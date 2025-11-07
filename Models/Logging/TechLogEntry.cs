namespace LayoutParserApi.Models.Logging
{
    public class TechLogEntry : LogEntry
    {
        public new Exception? Exception { get; set; }
        public string? MethodName { get; set; }
        public string? ClassName { get; set; }
        
        // StackTrace já está na classe base, não precisamos redeclarar
    }
}
