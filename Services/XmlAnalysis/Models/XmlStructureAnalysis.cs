namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    public class XmlStructureAnalysis
    {
        public string RootElement { get; set; }
        public int TotalElements { get; set; }
        public int TotalAttributes { get; set; }
        public int MaxDepth { get; set; }
        public List<string> Namespaces { get; set; } = new();
        public List<string> ElementTypes { get; set; } = new();
    }
}
