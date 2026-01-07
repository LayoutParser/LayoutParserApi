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
                mapperVo.IsNotExecuteTargetParser = isNotExecute;

            // Processar Rules
            var rulesElement = root.Element("Rules");
            if (rulesElement != null)
            {
                foreach (var ruleElement in rulesElement.Elements("Rule"))
                {
                    var rule = MapperRule.FromXml(ruleElement);
                    if (rule != null)
                        mapperVo.Rules.Add(rule);
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
                        mapperVo.LinkMappings.Add(linkMapping);
                }
            }

            // Tentar buscar XSL se existir
            var xslContentElement = root.Element("XslContent");
            if (xslContentElement != null && !string.IsNullOrEmpty(xslContentElement.Value))
                mapperVo.XslContent = xslContentElement.Value;
            else
            {
                var xslElement = root.Element("Xsl");
                if (xslElement != null && !string.IsNullOrEmpty(xslElement.Value))
                    mapperVo.XslContent = xslElement.Value;
            }

            return mapperVo;
        }
    }
}