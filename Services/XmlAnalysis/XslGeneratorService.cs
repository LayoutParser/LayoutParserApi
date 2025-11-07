using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Gera arquivo XSL a partir do MAP de transformação
    /// </summary>
    public class XslGeneratorService
    {
        private readonly ILogger<XslGeneratorService> _logger;

        public XslGeneratorService(ILogger<XslGeneratorService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gera arquivo XSL a partir do MAP
        /// </summary>
        public async Task<string> GenerateXslFromMapAsync(string mapXmlPath, string outputPath = null)
        {
            try
            {
                _logger.LogInformation("Iniciando geração de XSL a partir do MAP: {Path}", mapXmlPath);

                // Ler MAP XML
                var mapXml = await File.ReadAllTextAsync(mapXmlPath, Encoding.UTF8);
                var mapDoc = XDocument.Parse(mapXml);

                // Gerar XSL
                var xslContent = GenerateXslContent(mapDoc);

                // Salvar arquivo se outputPath fornecido
                if (!string.IsNullOrEmpty(outputPath))
                {
                    await File.WriteAllTextAsync(outputPath, xslContent, Encoding.UTF8);
                    _logger.LogInformation("Arquivo XSL gerado com sucesso: {Path}", outputPath);
                }

                return xslContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar XSL a partir do MAP");
                throw;
            }
        }

        /// <summary>
        /// Gera conteúdo XSL a partir do MAP
        /// </summary>
        private string GenerateXslContent(XDocument mapDoc)
        {
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

            // Processar regras do MAP
            var rules = mapDoc.Descendants("Rule").ToList();
            ProcessRules(sb, rules, mapDoc);

            sb.AppendLine("\t\t\t</infNFe>");
            sb.AppendLine("\t\t</NFe>");
            sb.AppendLine("\t</xsl:template>");
            sb.AppendLine();
            sb.AppendLine("</xsl:stylesheet>");

            return sb.ToString();
        }

        /// <summary>
        /// Processa regras do MAP e gera XSL correspondente
        /// </summary>
        private void ProcessRules(StringBuilder sb, List<XElement> rules, XDocument mapDoc)
        {
            // Agrupar regras por elemento de destino
            var rulesByTarget = rules
                .GroupBy(r => GetTargetElement(r))
                .ToList();

            foreach (var group in rulesByTarget)
            {
                var targetElement = group.Key;
                var targetRules = group.ToList();

                // Gerar elemento XML baseado no target
                GenerateXslElement(sb, targetElement, targetRules, mapDoc);
            }
        }

        /// <summary>
        /// Obtém elemento de destino de uma regra
        /// </summary>
        private string GetTargetElement(XElement rule)
        {
            // Procurar por TargetElementGuid ou analisar ContentValue
            var targetGuid = rule.Element("TargetElementGuid")?.Value;
            var contentValue = rule.Element("ContentValue")?.Value ?? "";

            // Extrair caminho de destino do ContentValue
            // Exemplo: T.enviNFe/NFe/infNFe/Id
            var match = System.Text.RegularExpressions.Regex.Match(
                contentValue,
                @"T\.([^\s=]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Fallback: usar nome da regra
            var ruleName = rule.Element("Name")?.Value ?? "Unknown";
            return MapRuleNameToElement(ruleName);
        }

        /// <summary>
        /// Mapeia nome da regra para elemento XML
        /// </summary>
        private string MapRuleNameToElement(string ruleName)
        {
            if (ruleName.Contains("chave") || ruleName.Contains("Chave")) return "ide";
            if (ruleName.Contains("Cabecalho") || ruleName.Contains("Cabecalho")) return "ide";
            if (ruleName.Contains("Emit")) return "emit";
            if (ruleName.Contains("Dest")) return "dest";
            if (ruleName.Contains("Det") || ruleName.Contains("Produto")) return "det";
            if (ruleName.Contains("Total") || ruleName.Contains("ICMS")) return "total";
            if (ruleName.Contains("Transp")) return "transp";
            if (ruleName.Contains("Cobr") || ruleName.Contains("Pag")) return "cobr";
            if (ruleName.Contains("InfAdic")) return "infAdic";

            return "ide"; // Default
        }

        /// <summary>
        /// Gera elemento XSL
        /// </summary>
        private void GenerateXslElement(StringBuilder sb, string targetElement, List<XElement> rules, XDocument mapDoc)
        {
            var elementPath = targetElement.Split('/');
            var elementName = elementPath.Last();

            // Gerar estrutura hierárquica
            foreach (var part in elementPath)
            {
                if (part == elementPath.Last())
                {
                    sb.AppendLine($"\t\t\t\t<{part}>");
                }
            }

            // Processar cada regra
            foreach (var rule in rules)
            {
                GenerateXslRule(sb, rule, mapDoc);
            }

            // Fechar elementos
            foreach (var part in elementPath.Reverse())
            {
                if (part == elementPath.Last())
                {
                    sb.AppendLine($"\t\t\t\t</{part}>");
                }
            }
        }

        /// <summary>
        /// Gera XSL para uma regra específica
        /// </summary>
        private void GenerateXslRule(StringBuilder sb, XElement rule, XDocument mapDoc)
        {
            var contentValue = rule.Element("ContentValue")?.Value ?? "";
            var ruleName = rule.Element("Name")?.Value ?? "";

            // Extrair mapeamentos do ContentValue
            // Formato: I.LINHA000/Campo ou I.LINHA001/Campo
            var mappings = ExtractMappings(contentValue);

            foreach (var mapping in mappings)
            {
                var sourcePath = mapping.Key; // Ex: I.LINHA000/Campo
                var targetPath = mapping.Value; // Ex: T.enviNFe/NFe/infNFe/ide/cUF

                // Converter para XPath e XSL
                var xpath = ConvertToXPath(sourcePath);
                var targetElement = GetElementFromPath(targetPath);

                // Gerar elemento XSL
                sb.AppendLine($"\t\t\t\t\t<{targetElement}>");
                sb.AppendLine($"\t\t\t\t\t\t<xsl:value-of select=\"{xpath}\"/>");
                sb.AppendLine($"\t\t\t\t\t</{targetElement}>");
            }
        }

        /// <summary>
        /// Extrai mapeamentos do ContentValue
        /// </summary>
        private Dictionary<string, string> ExtractMappings(string contentValue)
        {
            var mappings = new Dictionary<string, string>();

            // Procurar por padrões como: I.LINHA000/Campo = T.enviNFe/...
            var pattern = @"I\.([^/\s]+)/([^\s=]+)\s*=\s*T\.([^\s;]+)";
            var matches = System.Text.RegularExpressions.Regex.Matches(contentValue, pattern);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var source = $"I.{match.Groups[1].Value}/{match.Groups[2].Value}";
                var target = $"T.{match.Groups[3].Value}";
                mappings[source] = target;
            }

            return mappings;
        }

        /// <summary>
        /// Converte caminho de origem para XPath
        /// </summary>
        private string ConvertToXPath(string sourcePath)
        {
            // I.LINHA000/Campo -> ROOT/LINHA000/Campo
            var xpath = sourcePath.Replace("I.", "ROOT/");
            return xpath;
        }

        /// <summary>
        /// Obtém nome do elemento a partir do caminho
        /// </summary>
        private string GetElementFromPath(string targetPath)
        {
            // T.enviNFe/NFe/infNFe/ide/cUF -> cUF
            var parts = targetPath.Split('/');
            return parts.Last();
        }
    }
}

