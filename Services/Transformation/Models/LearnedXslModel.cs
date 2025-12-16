namespace LayoutParserApi.Services.Transformation.Models
{
    public class LearnedXslModel
    {
        public string LayoutName { get; set; }
        public List<LearnedPattern> Patterns { get; set; } = new();
        public List<TransformationRule> TransformationRules { get; set; } = new();
        public int ExamplesCount { get; set; }
        public DateTime LearnedAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }
    }
}
