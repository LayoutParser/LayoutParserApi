namespace LayoutParserApi.Services.Transformation.Models
{
    /// <summary>
    /// Resultado de similaridade
    /// </summary>
    public class SimilarityResult
    {
        public LearnedPattern Pattern { get; set; }
        public double Similarity { get; set; }
    }
}
