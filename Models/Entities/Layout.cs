using System.Xml.Serialization;

namespace LayoutParserApi.Models.Entities
{
    [XmlRoot("LayoutVO")]
    public class Layout
    {
        public string LayoutGuid { get; set; }
        public string LayoutType { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int LimitOfCaracters { get; set; }

        [XmlArray("Elements")]
        [XmlArrayItem("Element")]
        public List<LineElement> Elements { get; set; } = new();

        public int Delimiter { get; set; }
        public string Escape { get; set; }
        public string InitializerLine { get; set; }
        public string FinisherLine { get; set; }
        public bool WithBreakLines { get; set; }
    }
}