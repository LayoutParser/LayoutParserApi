using LayoutParserApi.Services.Transformation.Models;

namespace LayoutParserApi.Services.Testing.Models
{
    public class TestCaseResult
    {
        public string TestCaseName { get; set; }
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public string TransformedXml { get; set; }
        public string TclPath { get; set; }
        public string XslPath { get; set; }
        public TransformationValidationResult ValidationResult { get; set; }
        public ComparisonResult ComparisonResult { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
