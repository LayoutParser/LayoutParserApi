namespace LayoutParserApi.Models.Learning
{
    /// <summary>
    /// Resultado do processo de aprendizado
    /// </summary>
    public class LearningResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public LayoutModel LearnedModel { get; set; }
        public string ModelPath { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public TimeSpan ProcessingTime { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }
}