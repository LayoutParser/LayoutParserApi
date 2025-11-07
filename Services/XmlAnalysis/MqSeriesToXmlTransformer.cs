using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Transforma documentos MQSeries/IDOC em XML NFe para validação XSD
    /// Agora usa o pipeline de transformação: TXT → MAP → XML Intermediário → XSL → XML Final
    /// </summary>
    public class MqSeriesToXmlTransformer
    {
        private readonly ILogger<MqSeriesToXmlTransformer> _logger;
        private readonly string _transformationRulesPath;
        private readonly TransformationPipelineService _pipelineService;

        public MqSeriesToXmlTransformer(
            ILogger<MqSeriesToXmlTransformer> logger,
            IConfiguration configuration,
            TransformationPipelineService pipelineService)
        {
            _logger = logger;
            _transformationRulesPath = configuration["TransformationRules:Path"] 
                ?? @"C:\inetpub\wwwroot\layoutparser\TransformationRules";
            _pipelineService = pipelineService;
        }

        /// <summary>
        /// Transforma conteúdo MQSeries/IDOC em XML NFe usando pipeline: TXT → MAP → XSL → XML
        /// </summary>
        public async Task<TransformationResult> TransformToXmlAsync(string mqseriesContent, string layoutName, string transformationRulesXml = null)
        {
            var result = new TransformationResult
            {
                Success = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                SegmentMappings = new Dictionary<int, SegmentMapping>()
            };

            try
            {
                _logger.LogInformation("Iniciando transformação MQSeries/IDOC para XML usando pipeline. Layout: {LayoutName}", layoutName);

                // Usar pipeline de transformação: TXT → MAP → XML Intermediário → XSL → XML Final
                var pipelineResult = await _pipelineService.TransformTxtToXmlAsync(
                    mqseriesContent, 
                    layoutName, 
                    "NFe");

                if (!pipelineResult.Success)
                {
                    result.Success = false;
                    result.Errors.AddRange(pipelineResult.Errors);
                    result.Warnings.AddRange(pipelineResult.Warnings);
                    return result;
                }

                result.TransformedXml = pipelineResult.TransformedXml;
                result.Success = true;
                result.Warnings.AddRange(pipelineResult.Warnings);

                // Mapear segmentos se disponível
                foreach (var mapping in pipelineResult.SegmentMappings)
                {
                    result.SegmentMappings[mapping.Key] = new SegmentMapping
                    {
                        MqSeriesLineNumber = mapping.Key,
                        MqSeriesSegment = mapping.Value,
                        XmlElementPath = "NFe/infNFe",
                        XmlElement = null
                    };
                }

                _logger.LogInformation("Transformação pipeline concluída. Sucesso: {Success}", result.Success);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante transformação MQSeries para XML");
                result.Success = false;
                result.Errors.Add($"Erro interno: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Carrega regras de transformação do arquivo XML
        /// </summary>
        private async Task<XDocument> LoadTransformationRulesAsync(string layoutName)
        {
            try
            {
                // Procurar arquivo de regras baseado no nome do layout
                var rulesFileName = $"{layoutName}_transformation_rules.xml";
                var rulesPath = Path.Combine(_transformationRulesPath, rulesFileName);

                if (!File.Exists(rulesPath))
                {
                    // Tentar padrão genérico
                    rulesPath = Path.Combine(_transformationRulesPath, "mqseries_to_nfe_rules.xml");
                }

                if (File.Exists(rulesPath))
                {
                    var content = await File.ReadAllTextAsync(rulesPath);
                    return XDocument.Parse(content);
                }

                _logger.LogWarning("Arquivo de regras de transformação não encontrado: {Path}", rulesPath);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar regras de transformação");
                return null;
            }
        }

        /// <summary>
        /// Parseia conteúdo MQSeries/IDOC em linhas estruturadas
        /// </summary>
        private List<MqSeriesLine> ParseMqSeriesContent(string content)
        {
            var lines = new List<MqSeriesLine>();
            var contentLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

            for (int i = 0; i < contentLines.Length; i++)
            {
                var line = contentLines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Detectar tipo de linha MQSeries
                var lineType = DetectLineType(line);
                
                lines.Add(new MqSeriesLine
                {
                    LineNumber = i + 1,
                    Content = line,
                    Type = lineType,
                    SegmentName = ExtractSegmentName(line, lineType)
                });
            }

            return lines;
        }

        /// <summary>
        /// Detecta tipo de linha MQSeries/IDOC
        /// </summary>
        private string DetectLineType(string line)
        {
            if (line.Length >= 6)
            {
                var prefix = line.Substring(0, 6);
                
                // MQSeries: sequência numérica
                if (int.TryParse(prefix, out _))
                {
                    // Verificar tags comuns
                    if (line.Contains("HEADER")) return "HEADER";
                    if (line.Contains("TRAILER")) return "TRAILER";
                    if (line.Contains("LINHA")) return "LINHA";
                    return "DATA";
                }
            }

            // IDOC: prefixos específicos
            if (line.StartsWith("EDI_DC40")) return "EDI_DC40";
            if (line.StartsWith("ZRSDM_NFE")) return "ZRSDM_NFE";
            
            return "UNKNOWN";
        }

        /// <summary>
        /// Extrai nome do segmento da linha
        /// </summary>
        private string ExtractSegmentName(string line, string lineType)
        {
            // Para MQSeries, tentar extrair tag
            if (lineType == "HEADER" || lineType == "TRAILER")
                return lineType;

            // Para IDOC, usar o prefixo
            if (line.StartsWith("ZRSDM_NFE"))
            {
                var parts = line.Split(new[] { '|' }, StringSplitOptions.None);
                if (parts.Length > 0)
                    return parts[0];
            }

            return lineType;
        }

        /// <summary>
        /// Aplica regras de transformação para gerar XML
        /// </summary>
        private string ApplyTransformationRules(List<MqSeriesLine> mqseriesLines, XDocument rules, TransformationResult result)
        {
            try
            {
                // Criar estrutura XML base
                var nfeNamespace = XNamespace.Get("http://www.portalfiscal.inf.br/nfe");
                var nfe = new XElement(nfeNamespace + "NFe");
                nfe.SetAttributeValue(XName.Get("xmlns"), nfeNamespace.NamespaceName);
                
                var xsiNs = XNamespace.Xmlns + "xsi";
                nfe.SetAttributeValue(xsiNs, "http://www.w3.org/2001/XMLSchema-instance");

                // Processar cada linha MQSeries e aplicar regras
                foreach (var mqLine in mqseriesLines)
                {
                    try
                    {
                        // Encontrar regra de transformação para este segmento
                        var rule = FindTransformationRule(rules, mqLine.SegmentName, mqLine.Type);
                        
                        if (rule != null)
                        {
                            // Aplicar transformação
                            var xmlElement = ApplyRule(mqLine, rule, result);
                            if (xmlElement != null)
                            {
                                nfe.Add(xmlElement);
                                
                                // Mapear linha MQSeries -> elemento XML
                                result.SegmentMappings[mqLine.LineNumber] = new SegmentMapping
                                {
                                    MqSeriesLineNumber = mqLine.LineNumber,
                                    MqSeriesSegment = mqLine.SegmentName,
                                    XmlElementPath = xmlElement.Name.LocalName,
                                    XmlElement = xmlElement
                                };
                            }
                        }
                        else
                        {
                            result.Warnings.Add($"Linha {mqLine.LineNumber}: Nenhuma regra de transformação encontrada para segmento '{mqLine.SegmentName}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Linha {mqLine.LineNumber}: Erro ao aplicar transformação - {ex.Message}");
                    }
                }

                var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), nfe);
                return doc.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar regras de transformação");
                result.Errors.Add($"Erro ao aplicar transformação: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Encontra regra de transformação para um segmento
        /// </summary>
        private XElement FindTransformationRule(XDocument rules, string segmentName, string lineType)
        {
            // Procurar regra por nome de segmento
            var rule = rules.Descendants("rule")
                .FirstOrDefault(r => 
                    r.Attribute("mqseriesSegment")?.Value == segmentName ||
                    r.Attribute("lineType")?.Value == lineType);

            return rule;
        }

        /// <summary>
        /// Aplica uma regra de transformação específica
        /// </summary>
        private XElement ApplyRule(MqSeriesLine mqLine, XElement rule, TransformationResult result)
        {
            var targetElement = rule.Attribute("targetElement")?.Value ?? "unknown";
            var targetNamespace = rule.Attribute("targetNamespace")?.Value ?? "http://www.portalfiscal.inf.br/nfe";
            
            var ns = XNamespace.Get(targetNamespace);
            var element = new XElement(ns + targetElement);

            // Aplicar mapeamentos de campos
            var fieldMappings = rule.Descendants("fieldMapping");
            foreach (var mapping in fieldMappings)
            {
                var mqField = mapping.Attribute("mqseriesField")?.Value;
                var xmlField = mapping.Attribute("xmlField")?.Value;
                var startPos = int.Parse(mapping.Attribute("startPos")?.Value ?? "0");
                var length = int.Parse(mapping.Attribute("length")?.Value ?? "0");

                if (startPos > 0 && length > 0 && startPos + length <= mqLine.Content.Length)
                {
                    var value = mqLine.Content.Substring(startPos - 1, length).Trim();
                    element.SetElementValue(ns + xmlField, value);
                }
            }

            return element;
        }
    }

    /// <summary>
    /// Linha MQSeries/IDOC parseada
    /// </summary>
    public class MqSeriesLine
    {
        public int LineNumber { get; set; }
        public string Content { get; set; }
        public string Type { get; set; }
        public string SegmentName { get; set; }
    }

    /// <summary>
    /// Resultado da transformação
    /// </summary>
    public class TransformationResult
    {
        public bool Success { get; set; }
        public string TransformedXml { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public Dictionary<int, SegmentMapping> SegmentMappings { get; set; } = new();
    }

    /// <summary>
    /// Mapeamento de segmento MQSeries para elemento XML
    /// </summary>
    public class SegmentMapping
    {
        public int MqSeriesLineNumber { get; set; }
        public string MqSeriesSegment { get; set; }
        public string XmlElementPath { get; set; }
        public XElement XmlElement { get; set; }
    }
}

