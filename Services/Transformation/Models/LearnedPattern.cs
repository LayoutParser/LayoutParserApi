namespace LayoutParserApi.Services.Transformation.Models
{
    public class LearnedPattern
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public string Pattern { get; set; }
        public int Frequency { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}
