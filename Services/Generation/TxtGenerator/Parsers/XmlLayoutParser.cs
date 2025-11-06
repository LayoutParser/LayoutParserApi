using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using LayoutParserApi.Services.Generation.TxtGenerator.Models;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Parsers
{
    /// <summary>
    /// Parser para extrair definições de campos do layout XML
    /// </summary>
    public class XmlLayoutParser
    {
        private readonly ILogger<XmlLayoutParser> _logger;

        public XmlLayoutParser(ILogger<XmlLayoutParser> logger)
        {
            _logger = logger;
        }

        public FileLayout ParseLayout(string xmlContent)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;

                var fileLayout = new FileLayout
                {
                    LayoutName = root?.Element("Name")?.Value ?? "Unknown",
                    LayoutType = root?.Element("LayoutType")?.Value ?? "TextPositional",
                    LimitOfCharacters = int.TryParse(root?.Element("LimitOfCaracters")?.Value, out var limit) ? limit : 600
                };

                // Extrair linhas (Records)
                var lineElements = root?.Element("Elements")?.Elements("Element")
                    .Where(e => e.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value == "LineElementVO")
                    .OrderBy(e => int.TryParse(e.Element("Sequence")?.Value, out var seq) ? seq : int.MaxValue)
                    .ToList() ?? new List<XElement>();

                foreach (var lineElement in lineElements)
                {
                    var recordLayout = ParseRecordLayout(lineElement, fileLayout.LimitOfCharacters);
                    if (recordLayout != null)
                    {
                        fileLayout.Records.Add(recordLayout);
                    }
                }

                _logger.LogInformation("Layout XML parseado: {RecordCount} registros encontrados", fileLayout.Records.Count);
                return fileLayout;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao fazer parse do layout XML");
                throw;
            }
        }

        private RecordLayout ParseRecordLayout(XElement lineElement, int defaultLineLength)
        {
            var recordLayout = new RecordLayout
            {
                Name = lineElement.Element("Name")?.Value ?? "Unknown",
                InitialValue = lineElement.Element("InitialValue")?.Value ?? "",
                MinimalOccurrence = int.TryParse(lineElement.Element("MinimalOccurrence")?.Value, out var min) ? min : 1,
                MaximumOccurrence = int.TryParse(lineElement.Element("MaximumOccurrence")?.Value, out var max) ? max : 1,
                Sequence = int.TryParse(lineElement.Element("Sequence")?.Value, out var seq) ? seq : 0,
                TotalLength = defaultLineLength
            };

            // Extrair campos da linha
            var fieldElements = lineElement.Element("Elements")?.Elements("Element")
                .Where(e => e.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value == "FieldElementVO")
                .OrderBy(e => int.TryParse(e.Element("Sequence")?.Value, out var seq) ? seq : int.MaxValue)
                .ToList() ?? new List<XElement>();

            // Calcular posições considerando InitialValue e Sequencia
            int currentPosition = 0;
            bool isHeader = recordLayout.Name.Equals("HEADER", StringComparison.OrdinalIgnoreCase);

            if (!isHeader)
            {
                currentPosition = 6; // Sequencia da linha anterior (6 caracteres)
            }

            if (!string.IsNullOrEmpty(recordLayout.InitialValue))
            {
                currentPosition += recordLayout.InitialValue.Length;
            }

            foreach (var fieldElement in fieldElements)
            {
                var fieldName = fieldElement.Element("Name")?.Value;
                
                // Ignorar campo Sequencia (ele pertence à próxima linha)
                if (fieldName?.Equals("Sequencia", StringComparison.OrdinalIgnoreCase) == true)
                    continue;

                var length = int.TryParse(fieldElement.Element("LengthField")?.Value, out var len) ? len : 0;
                if (length <= 0)
                    continue;

                var startPosition = currentPosition;
                var endPosition = currentPosition + length - 1;

                var fieldDef = new FieldDefinition
                {
                    Name = fieldName ?? "Unknown",
                    StartPosition = startPosition,
                    EndPosition = endPosition,
                    DataType = InferDataType(fieldElement),
                    Alignment = fieldElement.Element("AlignmentType")?.Value ?? "Left",
                    IsRequired = bool.TryParse(fieldElement.Element("IsRequired")?.Value, out var req) && req,
                    Sequence = int.TryParse(fieldElement.Element("Sequence")?.Value, out var seq) ? seq : 0,
                    LineName = recordLayout.Name,
                    InitialValue = recordLayout.InitialValue
                };

                recordLayout.Fields.Add(fieldDef);
                currentPosition += length;
            }

            return recordLayout;
        }

        private string InferDataType(XElement fieldElement)
        {
            var fieldName = fieldElement.Element("Name")?.Value?.ToUpperInvariant() ?? "";
            var dataTypeGuid = fieldElement.Element("DataTypeGuid")?.Value ?? "";

            // Inferir tipo baseado no nome do campo
            if (fieldName.Contains("CNPJ"))
                return "cnpj";
            if (fieldName.Contains("CPF"))
                return "cpf";
            if (fieldName.Contains("DATA") || fieldName.Contains("DATE") || fieldName.Contains("EMISSAO"))
                return "date";
            if (fieldName.Contains("HORA") || fieldName.Contains("HOUR"))
                return "time";
            if (fieldName.Contains("VALOR") || fieldName.Contains("PRECO") || fieldName.Contains("TOTAL") || fieldName.Contains("AMOUNT"))
                return "decimal";
            if (fieldName.Contains("QUANTIDADE") || fieldName.Contains("QTD") || fieldName.Contains("SEQUENCIA") || fieldName.Contains("SEQUENCE"))
                return "int";
            if (fieldName.Contains("EMAIL"))
                return "email";
            if (fieldName.Contains("FILLER"))
                return "filler";

            return "string";
        }
    }
}

