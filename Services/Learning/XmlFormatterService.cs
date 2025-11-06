using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace LayoutParserApi.Services.Learning
{
    /// <summary>
    /// Serviço para formatação e visualização de XML (similar ao Notepad++ XML Tools)
    /// </summary>
    public class XmlFormatterService
    {
        private readonly ILogger<XmlFormatterService> _logger;

        public XmlFormatterService(ILogger<XmlFormatterService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Formata XML com indentação
        /// </summary>
        public string FormatXml(string xmlContent)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                    return xmlContent;

                var doc = XDocument.Parse(xmlContent);
                return doc.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao formatar XML, retornando conteúdo original");
                return xmlContent;
            }
        }

        /// <summary>
        /// Valida XML e retorna erros se houver
        /// </summary>
        public XmlValidationResult ValidateXml(string xmlContent)
        {
            var result = new XmlValidationResult
            {
                IsValid = true,
                Errors = new List<string>()
            };

            try
            {
                XDocument.Parse(xmlContent);
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Errors.Add(ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Gera estrutura hierárquica do XML para visualização
        /// </summary>
        public XmlStructure GenerateStructure(string xmlContent)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var structure = new XmlStructure
                {
                    RootElement = ExtractElementStructure(doc.Root)
                };

                return structure;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar estrutura XML");
                return new XmlStructure { RootElement = null };
            }
        }

        private XmlElementStructure ExtractElementStructure(XElement element)
        {
            var structure = new XmlElementStructure
            {
                Name = element.Name.LocalName,
                Attributes = element.Attributes().Select(a => new XmlAttributeInfo
                {
                    Name = a.Name.LocalName,
                    Value = a.Value
                }).ToList(),
                HasChildren = element.Elements().Any(),
                HasValue = !string.IsNullOrWhiteSpace(element.Value) && !element.Elements().Any(),
                Value = element.Elements().Any() ? null : element.Value
            };

            structure.Children = element.Elements().Select(ExtractElementStructure).ToList();

            return structure;
        }
    }

    public class XmlValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class XmlStructure
    {
        public XmlElementStructure RootElement { get; set; }
    }

    public class XmlElementStructure
    {
        public string Name { get; set; }
        public List<XmlAttributeInfo> Attributes { get; set; } = new();
        public List<XmlElementStructure> Children { get; set; } = new();
        public bool HasChildren { get; set; }
        public bool HasValue { get; set; }
        public string Value { get; set; }
    }

    public class XmlAttributeInfo
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }
}

