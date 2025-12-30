// Classes auxiliares para ML
using LayoutParserApi.Services.Validation;

public class DocumentPattern
{
    public string PatternId { get; set; } = "";
    public string LayoutGuid { get; set; } = "";
    public int DocumentLength { get; set; }
    public int LineCount { get; set; }
    public int ErrorsFound { get; set; }
    public Dictionary<string, object> Features { get; set; } = new();
    public List<PatternSuggestion>? Suggestions { get; set; }
    public double SuccessRate { get; set; }
    public double Confidence { get; set; }
    public DateTime CreatedAt { get; set; }
}