namespace LayoutParserApi.Services.Testing.Models
{
    public class TestResult
    {
        public string LayoutName { get; set; }
        public string LayoutGuid { get; set; }
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public List<TestCaseResult> TestCases { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
