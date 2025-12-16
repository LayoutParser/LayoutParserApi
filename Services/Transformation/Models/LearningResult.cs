namespace LayoutParserApi.Services.Transformation.Models
{
    // Modelos de dados
    public class LearningResult
    {
        public bool Success { get; set; }
        public List<LearnedPattern> PatternsLearned { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
