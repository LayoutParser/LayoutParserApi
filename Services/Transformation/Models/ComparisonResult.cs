public class ComparisonResult
{
    public bool Match { get; set; }
    public string Message { get; set; }
    public List<string> Differences { get; set; } = new();
}