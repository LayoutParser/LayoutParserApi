namespace LayoutParserApi.Models.Generation
{
    public class FieldGenerationStats
    {
        public string FieldName { get; set; }
        public string GenerationStrategy { get; set; }
        public int GeneratedCount { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public List<string> SampleValues { get; set; } = new();
    }
}
