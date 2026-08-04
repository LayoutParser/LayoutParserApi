using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;

using System.Xml;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class XmlLayoutLoader
    {
        public static async Task<Layout> LoadLayoutFromXmlAsync(Stream xmlStream)
        {
            try
            {
                using var reader = new StreamReader(xmlStream);
                var xmlContent = await reader.ReadToEndAsync();

                return LoadLayoutFromXmlString(xmlContent);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Erro ao carregar layout XML: {ex.Message}", ex);
            }
        }

        public static Layout LoadLayoutFromXmlString(string xmlContent)
        {
            try
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(xmlContent);

                var layout = new Layout();

                // Extrair informações básicas
                var layoutNode = xmlDoc.SelectSingleNode("//LayoutVO");
                if (layoutNode != null)
                {
                    layout.LayoutGuid = GetNodeValue(layoutNode, "LayoutGuid");
                    layout.LayoutType = GetNodeValue(layoutNode, "LayoutType");
                    layout.Name = GetNodeValue(layoutNode, "Name");
                    layout.Description = GetNodeValue(layoutNode, "Description");
                    layout.LimitOfCaracters = GetNodeIntValue(layoutNode, "LimitOfCaracters");
                    layout.Delimiter = GetNodeIntValue(layoutNode, "Delimiter");
                    layout.Escape = GetNodeValue(layoutNode, "Escape");
                    layout.InitializerLine = GetNodeValue(layoutNode, "InitializerLine");
                    layout.FinisherLine = GetNodeValue(layoutNode, "FinisherLine");
                    // ✅ Tri-estado: ausente vira null (layout legado), não false
                    layout.WithBreakLines = GetNodeNullableBoolValue(layoutNode, "WithBreakLines");

                    // Processar elementos
                    layout.Elements = ProcessElements(layoutNode.SelectNodes("Elements/Element"));
                }

                return layout;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Erro ao deserializar XML: {ex.Message}", ex);
            }
        }

        private static List<LineElement> ProcessElements(XmlNodeList elementNodes)
        {
            var elements = new List<LineElement>();

            if (elementNodes == null) return elements;

            foreach (XmlNode elementNode in elementNodes)
            {
                var xsiType = elementNode.Attributes?["xsi:type"]?.Value;

                if (xsiType == "LineElementVO")
                {
                    var lineElement = ProcessLineElement(elementNode);
                    if (lineElement != null)
                        elements.Add(lineElement);
                }
            }

            return elements;
        }

        private static LineElement ProcessLineElement(XmlNode lineNode)
        {
            var lineElement = new LineElement
            {
                ElementGuid = GetNodeValue(lineNode, "ElementGuid"),
                Description = GetNodeValue(lineNode, "Description"),
                Sequence = GetNodeIntValue(lineNode, "Sequence"),
                Name = GetNodeValue(lineNode, "Name"),
                IsRequired = GetNodeBoolValue(lineNode, "IsRequired"),
                MinimalOccurrence = GetNodeIntValue(lineNode, "MinimalOccurrence"),
                MaximumOccurrence = GetNodeIntValue(lineNode, "MaximumOccurrence"),
                InitialValue = GetNodeValue(lineNode, "InitialValue"),
                IsToValidateLengthCharacters = GetNodeBoolValue(lineNode, "IsToValidateLengthCharacters"),
                IsToValidateFieldLesserLength = GetNodeBoolValue(lineNode, "IsToValidateFieldLesserLength"),
                IsPositionalGroupRepetition = GetNodeBoolValue(lineNode, "IsPositionalGroupRepetition"),
                NotRealizeParser = GetNodeBoolValue(lineNode, "NotRealizeParser")
            };

            // Processar elementos filhos (campos)
            var childElements = lineNode.SelectNodes("Elements/Element");
            if (childElements != null)
            {
                lineElement.Elements = new List<string>();

                foreach (XmlNode childNode in childElements)
                {
                    var childXsiType = childNode.Attributes?["xsi:type"]?.Value;

                    if (childXsiType == "FieldElementVO")
                    {
                        var fieldElement = ProcessFieldElement(childNode);
                        if (fieldElement != null)
                        {
                            // Serializar FieldElement para JSON string
                            var json = Newtonsoft.Json.JsonConvert.SerializeObject(fieldElement);
                            lineElement.Elements.Add(json);
                        }
                    }
                    else if (childXsiType == "LineElementVO")
                    {
                        // Processar LineElement aninhado
                        var nestedLineElement = ProcessLineElement(childNode);
                        if (nestedLineElement != null)
                        {
                            var json = Newtonsoft.Json.JsonConvert.SerializeObject(nestedLineElement);
                            lineElement.Elements.Add(json);
                        }
                    }
                }
            }

            return lineElement;
        }

        private static FieldElement ProcessFieldElement(XmlNode fieldNode)
        {
            return new FieldElement
            {
                ElementGuid = GetNodeValue(fieldNode, "ElementGuid"),
                Description = GetNodeValue(fieldNode, "Description"),
                Sequence = GetNodeIntValue(fieldNode, "Sequence"),
                Name = GetNodeValue(fieldNode, "Name"),
                IsRequired = GetNodeBoolValue(fieldNode, "IsRequired"),
                StartValue = GetNodeIntValue(fieldNode, "StartValue"),
                IncrementValue = GetNodeIntValue(fieldNode, "IncrementValue"),
                LengthField = GetNodeIntValue(fieldNode, "LengthField"),
                AlignmentType = GetAlignmentType(GetNodeValue(fieldNode, "AlignmentType")),
                IsStaticValue = GetNodeBoolValue(fieldNode, "IsStaticValue"),
                IsCaseSensitiveValue = GetNodeBoolValue(fieldNode, "IsCaseSensitiveValue"),
                IsSequential = GetNodeBoolValue(fieldNode, "IsSequential"),
                RemoveWhiteSpaceType = GetRemoveWhiteSpaceType(GetNodeValue(fieldNode, "RemoveWhiteSpaceType")),
                DataTypeGuid = GetNodeValue(fieldNode, "DataTypeGuid")
            };
        }

        private static string GetNodeValue(XmlNode parentNode, string nodeName)
        {
            var node = parentNode.SelectSingleNode(nodeName);
            return node?.InnerText?.Trim() ?? string.Empty;
        }

        private static int GetNodeIntValue(XmlNode parentNode, string nodeName)
        {
            var value = GetNodeValue(parentNode, nodeName);
            return int.TryParse(value, out int result) ? result : 0;
        }

        private static bool GetNodeBoolValue(XmlNode parentNode, string nodeName)
        {
            var value = GetNodeValue(parentNode, nodeName);
            return bool.TryParse(value, out bool result) && result;
        }

        /// <summary>
        /// Versão tri-estado: devolve <c>null</c> quando o elemento está ausente ou malformado.
        /// Necessária para <c>WithBreakLines</c> — ver <c>Layout.WithBreakLines</c> e ADR-001.
        /// </summary>
        private static bool? GetNodeNullableBoolValue(XmlNode parentNode, string nodeName)
        {
            var value = GetNodeValue(parentNode, nodeName);
            return bool.TryParse(value, out bool result) ? result : null;
        }

        private static AlignmentType GetAlignmentType(string value)
        {
            return value?.ToLower() switch
            {
                "left" => AlignmentType.Left,
                "right" => AlignmentType.Right,
                "center" => AlignmentType.Center,
                _ => AlignmentType.Left
            };
        }

        private static RemoveWhiteSpaceType GetRemoveWhiteSpaceType(string value)
        {
            return value?.ToLower() switch
            {
                "all" => RemoveWhiteSpaceType.All,
                "start" => RemoveWhiteSpaceType.Start,
                "end" => RemoveWhiteSpaceType.End,
                "both" => RemoveWhiteSpaceType.Both,
                _ => RemoveWhiteSpaceType.None
            };
        }
    }
}