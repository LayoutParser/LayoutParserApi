using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace LayoutParserApi.Models.Entities
{
    /// <summary>
    /// ValueObject que representa a estrutura completa do MapperVO
    /// </summary>
    public class MapperVo
    {
        public string MapperGuid { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string InputLayoutGuid { get; set; }
        public string TargetLayoutGuid { get; set; }
        public List<MapperRule> Rules { get; set; } = new List<MapperRule>();
        public List<LinkMappingItem> LinkMappings { get; set; } = new List<LinkMappingItem>();
        public bool IsNotExecuteTargetParser { get; set; }
        public string XslContent { get; set; }

        /// <summary>
        /// Converte XML para MapperVo
        /// </summary>
        public static MapperVo FromXml(XDocument doc)
        {
            if (doc == null || doc.Root == null)
                return null;

            var mapperVo = new MapperVo();
            var root = doc.Root.Name.LocalName == "MapperVO" ? doc.Root : doc.Root.Element("MapperVO");
            
            if (root == null)
                root = doc.Root;

            mapperVo.MapperGuid = root.Element("MapperGuid")?.Value;
            mapperVo.Name = root.Element("Name")?.Value;
            mapperVo.Description = root.Element("Description")?.Value;
            mapperVo.InputLayoutGuid = root.Element("InputLayoutGuid")?.Value;
            mapperVo.TargetLayoutGuid = root.Element("TargetLayoutGuid")?.Value;
            
            var isNotExecuteTargetParserValue = root.Element("IsNotExecuteTargetParser")?.Value;
            if (bool.TryParse(isNotExecuteTargetParserValue, out var isNotExecute))
            {
                mapperVo.IsNotExecuteTargetParser = isNotExecute;
            }

            // Processar Rules
            var rulesElement = root.Element("Rules");
            if (rulesElement != null)
            {
                foreach (var ruleElement in rulesElement.Elements("Rule"))
                {
                    var rule = MapperRule.FromXml(ruleElement);
                    if (rule != null)
                    {
                        mapperVo.Rules.Add(rule);
                    }
                }
            }

            // Processar LinkMappings
            var linkMappingsElement = root.Element("LinkMappings");
            if (linkMappingsElement != null)
            {
                foreach (var linkMappingElement in linkMappingsElement.Elements("LinkMappingItem"))
                {
                    var linkMapping = LinkMappingItem.FromXml(linkMappingElement);
                    if (linkMapping != null)
                    {
                        mapperVo.LinkMappings.Add(linkMapping);
                    }
                }
            }

            // Tentar buscar XSL se existir
            var xslContentElement = root.Element("XslContent");
            if (xslContentElement != null && !string.IsNullOrEmpty(xslContentElement.Value))
            {
                mapperVo.XslContent = xslContentElement.Value;
            }
            else
            {
                var xslElement = root.Element("Xsl");
                if (xslElement != null && !string.IsNullOrEmpty(xslElement.Value))
                {
                    mapperVo.XslContent = xslElement.Value;
                }
            }

            return mapperVo;
        }
    }

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
            {
                rule.Sequence = sequenceInt;
            }

            // Parse IsRequired
            var isRequiredValue = ruleElement.Element("IsRequired")?.Value;
            if (bool.TryParse(isRequiredValue, out var isRequired))
            {
                rule.IsRequired = isRequired;
            }

            // Parse CreateOnlyChildren
            var createOnlyChildrenValue = ruleElement.Element("CreateOnlyChildren")?.Value;
            if (bool.TryParse(createOnlyChildrenValue, out var createOnlyChildren))
            {
                rule.CreateOnlyChildren = createOnlyChildren;
            }

            // Parse IsPrePosRule
            var isPrePosRuleValue = ruleElement.Element("IsPrePosRule")?.Value;
            if (bool.TryParse(isPrePosRuleValue, out var isPrePosRule))
            {
                rule.IsPrePosRule = isPrePosRule;
            }

            return rule;
        }
    }

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
            {
                linkMapping.Sequence = sequenceInt;
            }

            // Parse IsRequired
            var isRequiredValue = linkMappingElement.Element("IsRequired")?.Value;
            if (bool.TryParse(isRequiredValue, out var isRequired))
            {
                linkMapping.IsRequired = isRequired;
            }

            // Parse IsToTruncateValue
            var isToTruncateValue = linkMappingElement.Element("IsToTruncateValue")?.Value;
            if (bool.TryParse(isToTruncateValue, out var isToTruncate))
            {
                linkMapping.IsToTruncateValue = isToTruncate;
            }

            // Parse NotCreateGroupTagOnlyChilds
            var notCreateGroupTagOnlyChildsValue = linkMappingElement.Element("NotCreateGroupTagOnlyChilds")?.Value;
            if (bool.TryParse(notCreateGroupTagOnlyChildsValue, out var notCreateGroupTagOnlyChilds))
            {
                linkMapping.NotCreateGroupTagOnlyChilds = notCreateGroupTagOnlyChilds;
            }

            // Parse AllowEmpty
            var allowEmptyValue = linkMappingElement.Element("AllowEmpty")?.Value;
            if (bool.TryParse(allowEmptyValue, out var allowEmpty))
            {
                linkMapping.AllowEmpty = allowEmpty;
            }

            return linkMapping;
        }
    }
}

