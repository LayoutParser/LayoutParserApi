namespace LayoutParserApi.Models.Logging.Interface
{
    public interface ILogEntry
    {
        string RequestId { get; set; }
        DateTime Timestamp { get; set; }
        string Message { get; set; }
    }
}
