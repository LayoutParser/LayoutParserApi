using System.Xml.Serialization;

namespace LayoutParserApi.Models.Entities
{
    public class LineElement
    {
        [XmlAttribute("type", Namespace = "http://www.w3.org/2001/XMLSchema-instance")]
        public string Type { get; set; } = "LineElementVO";

        public string ElementGuid { get; set; }
        public string Description { get; set; }
        public int Sequence { get; set; }
        public string Name { get; set; }
        public bool IsRequired { get; set; }

        [XmlArray("Elements")]
        [XmlArrayItem("Element")]
        public List<string> Elements { get; set; } = new();
        public int MinimalOccurrence { get; set; }
        public int MaximumOccurrence { get; set; }
        public string InitialValue { get; set; }
        public bool IsToValidateLengthCharacters { get; set; }
        public bool IsToValidateFieldLesserLength { get; set; }
        public bool IsPositionalGroupRepetition { get; set; }
        public bool NotRealizeParser { get; set; }
        public List<object> DeserializedElements { get; internal set; }
    }
}