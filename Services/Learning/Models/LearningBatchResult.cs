namespace LayoutParserApi.Services.Learning.Models
{
    /// <summary>
    /// Resultado do aprendizado em lote
    /// </summary>
    public class LearningBatchResult
    {
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> LearnedLayouts { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }
}
