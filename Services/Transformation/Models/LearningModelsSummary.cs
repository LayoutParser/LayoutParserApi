namespace LayoutParserApi.Services.Transformation.Models
{
    /// <summary>
    /// Resumo agregado de todos os modelos aprendidos (TCL + XSL) persistidos em
    /// LearningModelsPath — usado pelo endpoint GET /api/metrics/learning/summary.
    /// </summary>
    public class LearningModelsSummary
    {
        public int TotalModels { get; set; }
        public int TotalPatterns { get; set; }
        public int TotalExamples { get; set; }
        public double AverageConfidence { get; set; }
    }
}
