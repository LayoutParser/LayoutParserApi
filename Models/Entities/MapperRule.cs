using System.Xml.Linq;

namespace LayoutParserApi.Models.Entities
{
    /// <summary>
    /// Representa uma Rule do MapperVO com código C#
    /// </summary>
    public class MapperRule
    {
        public string ElementGuid { get; set; }
        public string Description { get; set; }
        public int Sequence { get; set; }
        public string Name { get; set; }
        public bool IsRequired { get; set; }
        public string ContentValue { get; set; } // Código C# da regra
        public bool CreateOnlyChildren { get; set; }
        public bool IsPrePosRule { get; set; }
        public string TargetElementGuid { get; set; }

        public static MapperRule FromXml(XElement ruleElement)
        {
            if (ruleElement == null)
                return null;

            var rule = new MapperRule
            {
                ElementGuid = ruleElement.Element("ElementGuid")?.Value,
                Description = ruleElement.Element("Description")?.Value,
                Name = ruleElement.Element("Name")?.Value,
                ContentValue = ruleElement.Element("ContentValue")?.Value,
                TargetElementGuid = ruleElement.Element("TargetElementGuid")?.Value
            };

            // Parse Sequence
            var sequenceValue = ruleElement.Element("Sequence")?.Value;
            if (int.TryParse(sequenceValue, out var sequenceInt))
                rule.Sequence = sequenceInt;

            // Parse IsRequired
            var isRequiredValue = ruleElement.Element("IsRequired")?.Value;
            if (bool.TryParse(isRequiredValue, out var isRequired))
                rule.IsRequired = isRequired;

            // Parse CreateOnlyChildren
            var createOnlyChildrenValue = ruleElement.Element("CreateOnlyChildren")?.Value;
            if (bool.TryParse(createOnlyChildrenValue, out var createOnlyChildren))
                rule.CreateOnlyChildren = createOnlyChildren;

            // Parse IsPrePosRule
            var isPrePosRuleValue = ruleElement.Element("IsPrePosRule")?.Value;
            if (bool.TryParse(isPrePosRuleValue, out var isPrePosRule))
                rule.IsPrePosRule = isPrePosRule;

            return rule;
        }
    }
}