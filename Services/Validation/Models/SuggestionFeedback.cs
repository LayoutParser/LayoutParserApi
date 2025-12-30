public class SuggestionFeedback
{
    public string SuggestionId { get; set; } = "";
    public bool WasAccepted { get; set; }
    public string? ActualCorrection { get; set; }
    public DateTime Timestamp { get; set; }
}