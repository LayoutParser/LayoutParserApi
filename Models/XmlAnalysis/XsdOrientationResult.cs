namespace LayoutParserApi.Models.XmlAnalysis
{
    public class XsdOrientationResult
    {
        public bool Success { get; set; }
        public List<string> Orientations { get; set; } = new();
    }
}
