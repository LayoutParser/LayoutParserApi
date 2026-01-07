using System.Xml.Linq;

namespace LayoutParserApi.Models.Entities
{
    /// <summary>
    /// Representa um LinkMappingItem do MapperVO para mapeamento direto de campos
    /// </summary>
    public class LinkMappingItem
    {
        public string ElementGuid { get; set; }
        public string Description { get; set; }
        public int Sequence { get; set; }
        public string Name { get; set; }
        public bool IsRequired { get; set; }
        public string InputLayoutGuid { get; set; } // GUID do layout de entrada (pode ter prefixo GRT_)
        public string TargetLayoutGuid { get; set; } // GUID do layout de saída (pode ter prefixo GRT_)
        public bool IsToTruncateValue { get; set; }
        public string RemoveWhiteSpaceType { get; set; }
        public string DefaultValue { get; set; }
        public bool NotCreateGroupTagOnlyChilds { get; set; }
        public bool AllowEmpty { get; set; }

        public static LinkMappingItem FromXml(XElement linkMappingElement)
        {
            if (linkMappingElement == null)
                return null;

            var linkMapping = new LinkMappingItem
            {
                ElementGuid = linkMappingElement.Element("ElementGuid")?.Value,
                Description = linkMappingElement.Element("Description")?.Value,
                Name = linkMappingElement.Element("Name")?.Value,
                InputLayoutGuid = linkMappingElement.Element("InputLayoutGuid")?.Value,
                TargetLayoutGuid = linkMappingElement.Element("TargetLayoutGuid")?.Value,
                RemoveWhiteSpaceType = linkMappingElement.Element("RemoveWhiteSpaceType")?.Value,
                DefaultValue = linkMappingElement.Element("DefaultValue")?.Value
            };

            // Parse Sequence
            var sequenceValue = linkMappingElement.Element("Sequence")?.Value;
            if (int.TryParse(sequenceValue, out var sequenceInt))
                linkMapping.Sequence = sequenceInt;

            // Parse IsRequired
            var isRequiredValue = linkMappingElement.Element("IsRequired")?.Value;
            if (bool.TryParse(isRequiredValue, out var isRequired))
                linkMapping.IsRequired = isRequired;

            // Parse IsToTruncateValue
            var isToTruncateValue = linkMappingElement.Element("IsToTruncateValue")?.Value;
            if (bool.TryParse(isToTruncateValue, out var isToTruncate))
                linkMapping.IsToTruncateValue = isToTruncate;

            // Parse NotCreateGroupTagOnlyChilds
            var notCreateGroupTagOnlyChildsValue = linkMappingElement.Element("NotCreateGroupTagOnlyChilds")?.Value;
            if (bool.TryParse(notCreateGroupTagOnlyChildsValue, out var notCreateGroupTagOnlyChilds))
                linkMapping.NotCreateGroupTagOnlyChilds = notCreateGroupTagOnlyChilds;

            // Parse AllowEmpty
            var allowEmptyValue = linkMappingElement.Element("AllowEmpty")?.Value;
            if (bool.TryParse(allowEmptyValue, out var allowEmpty))
                linkMapping.AllowEmpty = allowEmpty;

            return linkMapping;
        }
    }
}