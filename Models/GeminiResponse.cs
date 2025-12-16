namespace LayoutParserApi.Models
{
    public class GeminiResponse
    {
        public GeminiCandidate[] candidates { get; set; } = Array.Empty<GeminiCandidate>();
    }
}
