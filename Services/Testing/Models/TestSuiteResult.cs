namespace LayoutParserApi.Services.Testing.Models
{
    // Modelos de resultado de teste
    public class TestSuiteResult
    {
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
        public int FailedTests { get; set; }
        public List<TestResult> TestResults { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }
}
