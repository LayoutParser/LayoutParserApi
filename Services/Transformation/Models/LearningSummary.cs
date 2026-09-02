namespace LayoutParserApi.Services.Transformation.Models
{
    /// <summary>
    /// Resumo agregado dos modelos de aprendizado (TCL + XSL) já persistidos em disco,
    /// entre todos os layouts — usado pelo endpoint <c>GET /api/metrics/learning/summary</c>.
    /// </summary>
    public class LearningSummary
    {
        /// <summary>Quantidade de arquivos de modelo aprendido (tcl_*.json + xsl_*.json) encontrados.</summary>
        public int TotalModels { get; set; }

        /// <summary>Soma de padrões aprendidos (<c>Patterns.Count</c>) entre todos os modelos.</summary>
        public int TotalPatterns { get; set; }

        /// <summary>Soma de <c>ExamplesCount</c> entre todos os modelos.</summary>
        public int TotalExamples { get; set; }

        /// <summary>
        /// Média de confiança entre todos os padrões de todos os modelos (0 se nenhum padrão existir).
        /// </summary>
        public double AverageConfidence { get; set; }
    }
}
