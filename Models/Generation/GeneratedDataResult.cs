using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Models.Generation
{
    public class GeneratedDataResult
    {
        public bool Success { get; set; }
        public List<string> GeneratedLines { get; set; } = new();
        public string ErrorMessage { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public int TotalRecords { get; set; }
        public Layout UsedLayout { get; set; }
        public Dictionary<string, object> GenerationMetadata { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<FieldGenerationStats> FieldStats { get; set; } = new();
    }
}