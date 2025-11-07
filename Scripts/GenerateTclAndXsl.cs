using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Scripts
{
    /// <summary>
    /// Script para gerar TCL e XSL a partir do layout XML e MAP
    /// </summary>
    public class GenerateTclAndXsl
    {
        private readonly ILogger _logger;

        public GenerateTclAndXsl(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gera TCL a partir do layout XML MQSeries
        /// </summary>
        public static async Task<string> GenerateTclFromLayout(string layoutXmlPath)
        {
            try
            {
                var layoutXml = await File.ReadAllTextAsync(layoutXmlPath, Encoding.UTF8);
                var layoutDoc = XDocument.Parse(layoutXml);

                var sb = new StringBuilder();
                sb.AppendLine("<MAP>");

                // Namespace para xsi:type
                XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

                // Extrair todas as linhas
                var lineElements = layoutDoc.Descendants()
                    .Where(e => e.Attribute(xsi + "type")?.Value == "LineElementVO")
                    .OrderBy(e => int.TryParse(e.Descendants().FirstOrDefault(d => d.Name.LocalName == "Sequence")?.Value ?? "0", out var seq) ? seq : 0)
                    .ToList();

                foreach (var lineElement in lineElements)
                {
                    var lineName = lineElement.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                    if (string.IsNullOrEmpty(lineName)) continue;

                    // Obter identificador
                    var identifier = GetLineIdentifier(lineName);

                    sb.AppendLine($"\t<LINE identifier=\"{identifier}\" name=\"{lineName}\">");

                    // Extrair campos desta linha
                    var fields = lineElement.Descendants()
                        .Where(e => e.Attribute(xsi + "type")?.Value == "FieldElementVO")
                        .OrderBy(e => int.TryParse(e.Descendants().FirstOrDefault(d => d.Name.LocalName == "Sequence")?.Value ?? "0", out var seq) ? seq : 0)
                        .ToList();

                    int currentPos = 1;
                    foreach (var field in fields)
                    {
                        var fieldName = field.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                        var lengthStr = field.Descendants().FirstOrDefault(e => e.Name.LocalName == "LengthField")?.Value;
                        
                        if (string.IsNullOrEmpty(fieldName) || !int.TryParse(lengthStr, out var length))
                            continue;

                        // Sanitizar nome do campo
                        var sanitizedName = SanitizeFieldName(fieldName);

                        // Detectar se é decimal
                        var isDecimal = IsDecimalField(fieldName);
                        var lengthAttr = length.ToString();
                        if (isDecimal)
                        {
                            lengthAttr = $"{length},2,0"; // Padrão 2 casas decimais
                        }

                        sb.AppendLine($"\t\t<FIELD name=\"{sanitizedName}\" length=\"{lengthAttr}\"/>");
                        currentPos += length;
                    }

                    // Verificar filhos
                    var children = layoutDoc.Descendants()
                        .Where(e => e.Attribute(xsi + "type")?.Value == "LineElementVO")
                        .Where(e => e.Descendants().FirstOrDefault(d => d.Name.LocalName == "ParentElement")?.Value?.Contains(lineName) == true)
                        .Select(e => e.Descendants().FirstOrDefault(d => d.Name.LocalName == "Name")?.Value)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Distinct()
                        .ToList();

                    foreach (var child in children)
                    {
                        sb.AppendLine($"\t\t<CHILD>{child}</CHILD>");
                    }

                    sb.AppendLine("\t</LINE>");
                    sb.AppendLine();
                }

                sb.AppendLine("</MAP>");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                throw new Exception($"Erro ao gerar TCL: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gera XSL a partir do MAP
        /// </summary>
        public static async Task<string> GenerateXslFromMap(string mapXmlPath)
        {
            try
            {
                var mapXml = await File.ReadAllTextAsync(mapXmlPath, Encoding.UTF8);
                var mapDoc = XDocument.Parse(mapXml);

                var sb = new StringBuilder();
                
                // Cabeçalho XSL
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                sb.AppendLine("<xsl:stylesheet version=\"1.0\"");
                sb.AppendLine("\txmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"");
                sb.AppendLine("\txmlns:ng=\"com.neogrid.integrator.XSLFunctions\"");
                sb.AppendLine("\texclude-result-prefixes=\"ng\"");
                sb.AppendLine("\textension-element-prefixes=\"ng\">");
                sb.AppendLine();
                sb.AppendLine("\t<xsl:output method=\"xml\" encoding=\"UTF-8\" indent=\"yes\"/>");
                sb.AppendLine();

                // Template raiz
                sb.AppendLine("\t<xsl:template match=\"/\">");
                sb.AppendLine("\t\t<NFe xmlns=\"http://www.portalfiscal.inf.br/nfe\">");
                sb.AppendLine("\t\t\t<infNFe versao=\"4.00\">");
                sb.AppendLine("\t\t\t\t<xsl:attribute name=\"Id\">");
                sb.AppendLine("\t\t\t\t\t<xsl:text>NFe</xsl:text>");
                sb.AppendLine("\t\t\t\t\t<xsl:value-of select=\"normalize-space(ROOT/chave/chNFe)\"/>");
                sb.AppendLine("\t\t\t\t</xsl:attribute>");
                sb.AppendLine();

                // Processar regras e gerar estrutura
                var rules = mapDoc.Descendants("Rule")
                    .OrderBy(r => int.TryParse(r.Element("Sequence")?.Value ?? "0", out var seq) ? seq : 0)
                    .ToList();

                GenerateXslFromRules(sb, rules);

                sb.AppendLine("\t\t\t</infNFe>");
                sb.AppendLine("\t\t</NFe>");
                sb.AppendLine("\t</xsl:template>");
                sb.AppendLine();
                sb.AppendLine("</xsl:stylesheet>");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                throw new Exception($"Erro ao gerar XSL: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gera XSL a partir das regras do MAP
        /// </summary>
        private static void GenerateXslFromRules(StringBuilder sb, List<XElement> rules)
        {
            // Agrupar mapeamentos por elemento XML de destino
            var mappings = new Dictionary<string, List<FieldMapping>>();

            foreach (var rule in rules)
            {
                var contentValue = rule.Element("ContentValue")?.Value ?? "";
                ExtractMappingsFromRule(contentValue, mappings);
            }

            // Gerar estrutura XML hierárquica
            GenerateXslStructure(sb, mappings);
        }

        /// <summary>
        /// Extrai mapeamentos de uma regra
        /// </summary>
        private static void ExtractMappingsFromRule(string contentValue, Dictionary<string, List<FieldMapping>> mappings)
        {
            // Padrão: I.LINHA000/Campo ou I.LINHA001/Campo = T.enviNFe/NFe/infNFe/ide/cUF
            // Ou: #.campo = I.LINHA000/Campo; T.enviNFe/NFe/infNFe/ide/cUF = #.campo;

            var patterns = new[]
            {
                @"I\.([^/]+)/([^\s=;]+)\s*=\s*T\.([^\s;]+)",  // I.LINHA000/Campo = T.path
                @"T\.([^\s=;]+)\s*=\s*[^;]*I\.([^/]+)/([^\s;]+)",  // T.path = ... I.LINHA000/Campo
            };

            foreach (var pattern in patterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(contentValue, pattern);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    string sourceLine, sourceField, targetPath;
                    
                    if (pattern.Contains("I\\.") && match.Groups.Count >= 4)
                    {
                        sourceLine = match.Groups[1].Value;
                        sourceField = match.Groups[2].Value;
                        targetPath = match.Groups[3].Value;
                    }
                    else if (match.Groups.Count >= 4)
                    {
                        targetPath = match.Groups[1].Value;
                        sourceLine = match.Groups[2].Value;
                        sourceField = match.Groups[3].Value;
                    }
                    else
                    {
                        continue;
                    }

                    var elementPath = targetPath.Split('/').Last();
                    var parentPath = string.Join("/", targetPath.Split('/').Take(targetPath.Split('/').Length - 1));

                    if (!mappings.ContainsKey(parentPath))
                    {
                        mappings[parentPath] = new List<FieldMapping>();
                    }

                    mappings[parentPath].Add(new FieldMapping
                    {
                        SourceLine = sourceLine,
                        SourceField = sourceField,
                        TargetElement = elementPath,
                        TargetPath = targetPath
                    });
                }
            }
        }

        /// <summary>
        /// Gera estrutura XSL
        /// </summary>
        private static void GenerateXslStructure(StringBuilder sb, Dictionary<string, List<FieldMapping>> mappings)
        {
            // Elementos principais da NFe
            var mainElements = new[] { "ide", "emit", "dest", "det", "total", "transp", "cobr", "infAdic" };

            foreach (var element in mainElements)
            {
                var elementMappings = mappings
                    .Where(m => m.Key.Contains(element) || m.Value.Any(v => v.TargetPath.Contains(element)))
                    .SelectMany(m => m.Value.Where(v => v.TargetPath.Contains(element)))
                    .ToList();

                if (elementMappings.Any())
                {
                    GenerateXslElement(sb, element, elementMappings, 4);
                }
            }
        }

        /// <summary>
        /// Gera elemento XSL
        /// </summary>
        private static void GenerateXslElement(StringBuilder sb, string elementName, List<FieldMapping> mappings, int indent)
        {
            var indentStr = new string('\t', indent);
            sb.AppendLine($"{indentStr}<{elementName}>");

            foreach (var mapping in mappings.Where(m => m.TargetElement == elementName || m.TargetPath.EndsWith("/" + elementName)))
            {
                var xpath = $"ROOT/{mapping.SourceLine}/{mapping.SourceField}";
                sb.AppendLine($"{indentStr}\t<{mapping.TargetElement}>");
                sb.AppendLine($"{indentStr}\t\t<xsl:value-of select=\"{xpath}\"/>");
                sb.AppendLine($"{indentStr}\t</{mapping.TargetElement}>");
            }

            sb.AppendLine($"{indentStr}</{elementName}>");
        }

        private static string GetLineIdentifier(string lineName)
        {
            if (lineName == "HEADER") return "HEADER";
            if (lineName == "TRAILER") return "TRAILER";

            if (lineName.StartsWith("LINHA"))
            {
                var num = lineName.Replace("LINHA", "");
                if (int.TryParse(num, out var n))
                {
                    // Mapear para letras: 000->A, 001->B, etc.
                    if (n < 26) return ((char)('A' + n)).ToString();
                    if (n < 52) return "A" + ((char)('A' + (n - 26))).ToString();
                    return "Z" + num;
                }
            }

            return lineName.Substring(0, Math.Min(1, lineName.Length)).ToUpper();
        }

        private static string SanitizeFieldName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return "Unknown";

            // Converter para camelCase
            var parts = fieldName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return fieldName;

            var result = char.ToLower(parts[0][0]) + parts[0].Substring(1);
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    result += char.ToUpper(parts[i][0]) + parts[i].Substring(1);
                }
            }

            return result;
        }

        private static bool IsDecimalField(string fieldName)
        {
            return fieldName.Contains("Valor") || fieldName.Contains("Preco") || 
                   fieldName.Contains("Total") || fieldName.Contains("vBC") || 
                   fieldName.Contains("vICMS") || fieldName.Contains("vST") ||
                   (fieldName.Length > 1 && fieldName.StartsWith("v") && char.IsUpper(fieldName[1]));
        }

        private class FieldMapping
        {
            public string SourceLine { get; set; }
            public string SourceField { get; set; }
            public string TargetElement { get; set; }
            public string TargetPath { get; set; }
        }
    }
}

