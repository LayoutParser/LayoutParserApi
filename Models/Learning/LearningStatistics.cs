namespace LayoutParserApi.Models.Learning
{
    /// <summary>
    /// Estatísticas de aprendizado
    /// </summary>
    public class LearningStatistics
    {
        public int TotalSamples { get; set; }
        public int ValidSamples { get; set; }
        public int InvalidSamples { get; set; }
        public double Accuracy { get; set; }
        public Dictionary<string, int> DataTypeDistribution { get; set; } = new();
        public Dictionary<string, double> FieldConfidence { get; set; } = new();
        public List<string> DetectedPatterns { get; set; } = new();
    }
}