using LayoutParserApi.Models.Enums;

using System.Xml.Serialization;

namespace LayoutParserApi.Models.Entities
{
    public class FieldElement
    {
        [XmlAttribute("type", Namespace = "http://www.w3.org/2001/XMLSchema-instance")]
        public string Type { get; set; } = "FieldElementVO";

        public string ElementGuid { get; set; }
        public string Description { get; set; }
        public int Sequence { get; set; }
        public string Name { get; set; }
        public bool IsRequired { get; set; }
        public int StartValue { get; set; }
        public int IncrementValue { get; set; }
        public int LengthField { get; set; }
        public AlignmentType AlignmentType { get; set; }
        public bool IsStaticValue { get; set; }
        public bool IsCaseSensitiveValue { get; set; }
        public bool IsSequential { get; set; }
        public RemoveWhiteSpaceType RemoveWhiteSpaceType { get; set; }
        public string DataTypeGuid { get; set; }
    }
}