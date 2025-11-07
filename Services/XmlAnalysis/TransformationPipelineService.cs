using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Serviço de pipeline de transformação: TXT → MAP/TCL → XML Intermediário → XSL → XML Final
    /// </summary>
    public class TransformationPipelineService
    {
        private readonly ILogger<TransformationPipelineService> _logger;
        private readonly string _tclBasePath;
        private readonly string _xslBasePath;
        private readonly string _mappingBasePath;

        public TransformationPipelineService(
            ILogger<TransformationPipelineService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _tclBasePath = configuration["TransformationPipeline:TclPath"] 
                ?? @"C:\inetpub\wwwroot\layoutparser\TCL";
            _xslBasePath = configuration["TransformationPipeline:XslPath"] 
                ?? @"C:\inetpub\wwwroot\layoutparser\XSL";
            _mappingBasePath = configuration["TransformationPipeline:MappingPath"] 
                ?? @"C:\inetpub\wwwroot\layoutparser\Mapeamentro";
        }

        /// <summary>
        /// Transforma TXT MQSeries/IDOC → MAP → XML Intermediário → XSL → XML NFe
        /// </summary>
        public async Task<TransformationPipelineResult> TransformTxtToXmlAsync(
            string txtContent, 
            string layoutName, 
            string targetDocumentType = "NFe")
        {
            var result = new TransformationPipelineResult
            {
                Success = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                StepResults = new Dictionary<string, string>()
            };

            try
            {
                _logger.LogInformation("Iniciando pipeline de transformação TXT → XML. Layout: {LayoutName}, Target: {TargetType}", 
                    layoutName, targetDocumentType);

                // Etapa 1: TXT → XML Intermediário (usando MAP/TCL)
                var intermediateXml = await TransformTxtToIntermediateXmlAsync(txtContent, layoutName, result);
                if (intermediateXml == null)
                {
                    result.Success = false;
                    return result;
                }

                result.StepResults["IntermediateXml"] = intermediateXml;
                _logger.LogInformation("XML intermediário gerado com sucesso");

                // Etapa 2: XML Intermediário → XML Final (usando XSL)
                var finalXml = await TransformIntermediateToFinalXmlAsync(intermediateXml, layoutName, targetDocumentType, result);
                if (finalXml == null)
                {
                    result.Success = false;
                    return result;
                }

                // Preencher caminhos TCL e XSL no resultado
                var tclFile = Path.Combine(_tclBasePath, $"{layoutName}.tcl");
                if (File.Exists(tclFile))
                {
                    result.TclPath = tclFile;
                }

                result.StepResults["FinalXml"] = finalXml;
                result.TransformedXml = finalXml;
                result.Success = true;

                _logger.LogInformation("Pipeline de transformação concluído com sucesso");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante pipeline de transformação");
                result.Success = false;
                result.Errors.Add($"Erro interno: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Transforma XML → XSL → XML Final (para transformações XML→XML)
        /// </summary>
        public async Task<TransformationPipelineResult> TransformXmlToXmlAsync(
            string xmlContent, 
            string sourceDocumentType, 
            string targetDocumentType,
            string layoutName = null)
        {
            var result = new TransformationPipelineResult
            {
                Success = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                StepResults = new Dictionary<string, string>()
            };

            try
            {
                _logger.LogInformation("Iniciando transformação XML → XML. Source: {SourceType}, Target: {TargetType}", 
                    sourceDocumentType, targetDocumentType);

                // Carregar XSL apropriado
                var xslPath = FindXslFile(sourceDocumentType, targetDocumentType, layoutName);
                if (string.IsNullOrEmpty(xslPath) || !File.Exists(xslPath))
                {
                    result.Success = false;
                    result.Errors.Add($"Arquivo XSL não encontrado para transformação {sourceDocumentType} → {targetDocumentType}");
                    return result;
                }

                result.XslPath = xslPath;

                // Aplicar transformação XSL
                var finalXml = await ApplyXsltTransformAsync(xmlContent, xslPath, result);
                if (finalXml == null)
                {
                    result.Success = false;
                    return result;
                }

                result.StepResults["FinalXml"] = finalXml;
                result.TransformedXml = finalXml;
                result.Success = true;

                _logger.LogInformation("Transformação XML → XML concluída com sucesso");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante transformação XML → XML");
                result.Success = false;
                result.Errors.Add($"Erro interno: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Etapa 1: Transforma TXT MQSeries/IDOC em XML Intermediário usando MAP
        /// </summary>
        private async Task<string> TransformTxtToIntermediateXmlAsync(
            string txtContent, 
            string layoutName, 
            TransformationPipelineResult result)
        {
            try
            {
                // Carregar arquivo MAP
                var mapContent = await LoadMappingFileAsync(layoutName);
                if (mapContent == null)
                {
                    result.Errors.Add($"Arquivo MAP não encontrado para layout: {layoutName}");
                    return null;
                }

                // Parsear MAP para entender estrutura
                var mapDocument = XDocument.Parse(mapContent);
                
                // Parsear TXT em linhas
                var txtLines = txtContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                // Gerar XML intermediário baseado no MAP
                var intermediateXml = GenerateIntermediateXmlFromMap(txtLines, mapDocument, result);
                
                return intermediateXml;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar TXT para XML intermediário");
                result.Errors.Add($"Erro na transformação TXT → XML Intermediário: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gera XML intermediário a partir do TXT e do MAP
        /// </summary>
        private string GenerateIntermediateXmlFromMap(
            List<string> txtLines, 
            XDocument mapDocument, 
            TransformationPipelineResult result)
        {
            try
            {
                var root = new XElement("ROOT");
                var currentLineIndex = 0;

                // Processar cada linha do TXT
                foreach (var txtLine in txtLines)
                {
                    if (string.IsNullOrWhiteSpace(txtLine))
                        continue;

                    // Detectar tipo de linha baseado no identificador (primeiros caracteres ou padrão)
                    var lineIdentifier = DetectLineIdentifier(txtLine);
                    
                    // Encontrar definição correspondente no MAP
                    var lineDefinition = mapDocument.Descendants("LINE")
                        .FirstOrDefault(l => l.Attribute("identifier")?.Value == lineIdentifier ||
                                            l.Attribute("name")?.Value == lineIdentifier);

                    if (lineDefinition != null)
                    {
                        // Extrair campos da linha baseado na definição do MAP
                        var lineElement = ExtractLineFromTxt(txtLine, lineDefinition);
                        if (lineElement != null)
                        {
                            root.Add(lineElement);
                        }
                    }
                    else
                    {
                        result.Warnings.Add($"Linha {currentLineIndex + 1}: Identificador '{lineIdentifier}' não encontrado no MAP");
                    }

                    currentLineIndex++;
                }

                // Adicionar elemento chave se necessário (geralmente vem da primeira linha)
                if (root.Elements().Any(e => e.Name.LocalName == "chave"))
                {
                    // Chave já foi processada
                }
                else if (txtLines.Count > 0)
                {
                    // Tentar extrair chave da primeira linha
                    var chaveElement = new XElement("chave");
                    // Lógica para extrair chave da primeira linha
                    root.AddFirst(chaveElement);
                }

                var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
                return doc.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar XML intermediário");
                throw;
            }
        }

        /// <summary>
        /// Detecta identificador da linha (primeiro caractere ou padrão específico)
        /// </summary>
        private string DetectLineIdentifier(string txtLine)
        {
            if (string.IsNullOrWhiteSpace(txtLine))
                return "UNKNOWN";

            // Para MQSeries: pode ter identificador no início (ex: "A", "B", "H")
            // Ou pode ser baseado no conteúdo (ex: "HEADER", "TRAILER")
            if (txtLine.Length >= 1)
            {
                var firstChar = txtLine[0];
                if (char.IsLetter(firstChar))
                {
                    // Verificar se é um identificador simples (A, B, C, etc.)
                    if (txtLine.Length > 1 && (char.IsDigit(txtLine[1]) || char.IsWhiteSpace(txtLine[1])))
                    {
                        return firstChar.ToString();
                    }
                }
            }

            // Verificar padrões conhecidos
            if (txtLine.Contains("HEADER")) return "HEADER";
            if (txtLine.Contains("TRAILER")) return "TRAILER";
            if (txtLine.Contains("LINHA000")) return "LINHA000";
            if (txtLine.Contains("LINHA001")) return "LINHA001";

            // Padrão IDOC
            if (txtLine.StartsWith("EDI_DC40")) return "EDI_DC40";
            if (txtLine.StartsWith("ZRSDM_NFE")) return "ZRSDM_NFE";

            return "UNKNOWN";
        }

        /// <summary>
        /// Extrai campos de uma linha TXT baseado na definição do MAP
        /// </summary>
        private XElement ExtractLineFromTxt(string txtLine, XElement lineDefinition)
        {
            var lineName = lineDefinition.Attribute("name")?.Value ?? "Line";
            var lineElement = new XElement(lineName);
            
            var fields = lineDefinition.Descendants("FIELD").ToList();
            var currentPosition = 0;

            foreach (var fieldDef in fields)
            {
                var fieldName = fieldDef.Attribute("name")?.Value;
                var lengthAttr = fieldDef.Attribute("length")?.Value;
                
                if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(lengthAttr))
                    continue;

                // Parsear length (pode ser "60" ou "15,2,0" para decimais)
                var lengthParts = lengthAttr.Split(',');
                var fieldLength = int.Parse(lengthParts[0]);

                if (currentPosition + fieldLength <= txtLine.Length)
                {
                    var fieldValue = txtLine.Substring(currentPosition, fieldLength).Trim();
                    lineElement.Add(new XElement(fieldName, fieldValue));
                    currentPosition += fieldLength;
                }
            }

            return lineElement;
        }

        /// <summary>
        /// Etapa 2: Transforma XML Intermediário em XML Final usando XSL
        /// </summary>
        private async Task<string> TransformIntermediateToFinalXmlAsync(
            string intermediateXml, 
            string layoutName, 
            string targetDocumentType,
            TransformationPipelineResult result)
        {
            try
            {
                // Encontrar arquivo XSL apropriado
                var xslPath = FindXslFile("Intermediate", targetDocumentType, layoutName);
                if (string.IsNullOrEmpty(xslPath) || !File.Exists(xslPath))
                {
                    result.Errors.Add($"Arquivo XSL não encontrado para transformação Intermediate → {targetDocumentType}");
                    return null;
                }

                // Armazenar caminho XSL no resultado
                result.XslPath = xslPath;

                // Aplicar transformação XSL
                var finalXml = await ApplyXsltTransformAsync(intermediateXml, xslPath, result);
                return finalXml;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar XML intermediário para XML final");
                result.Errors.Add($"Erro na transformação XML Intermediário → XML Final: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Aplica transformação XSLT
        /// </summary>
        private async Task<string> ApplyXsltTransformAsync(
            string xmlContent, 
            string xslPath, 
            TransformationPipelineResult result)
        {
            try
            {
                // Configurar XslCompiledTransform
                var xslt = new XslCompiledTransform();
                xslt.Load(xslPath, new XsltSettings { EnableDocumentFunction = true }, new XmlUrlResolver());

                // Carregar XML de entrada
                using (var xmlReader = XmlReader.Create(new StringReader(xmlContent)))
                using (var stringWriter = new StringWriter())
                using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = false,
                    Encoding = Encoding.UTF8
                }))
                {
                    // Aplicar transformação
                    xslt.Transform(xmlReader, xmlWriter);
                    return stringWriter.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar transformação XSLT");
                result.Errors.Add($"Erro na transformação XSLT: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Carrega arquivo MAP
        /// </summary>
        private async Task<string> LoadMappingFileAsync(string layoutName)
        {
            try
            {
                // Procurar arquivo MAP baseado no nome do layout
                var mapFileName = $"MAP_{layoutName}.xml";
                var mapPath = Path.Combine(_mappingBasePath, mapFileName);

                if (!File.Exists(mapPath))
                {
                    // Tentar padrão alternativo
                    mapPath = Path.Combine(_mappingBasePath, "MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.xml");
                }

                if (File.Exists(mapPath))
                {
                    return await File.ReadAllTextAsync(mapPath, Encoding.UTF8);
                }

                _logger.LogWarning("Arquivo MAP não encontrado: {Path}", mapPath);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar arquivo MAP");
                return null;
            }
        }

        /// <summary>
        /// Encontra arquivo XSL apropriado
        /// </summary>
        private string FindXslFile(string sourceType, string targetType, string layoutName = null)
        {
            try
            {
                // Tentar padrões de nome de arquivo
                var patterns = new List<string>();

                if (!string.IsNullOrEmpty(layoutName))
                {
                    patterns.Add($"{layoutName}_*.xsl");
                    patterns.Add($"{targetType}*_{layoutName}.xsl");
                }

                patterns.Add($"{targetType}*{sourceType}*.xsl");
                patterns.Add($"{sourceType}_To_{targetType}.xsl");
                patterns.Add($"NFe*_{targetType}*.xsl");

                foreach (var pattern in patterns)
                {
                    var files = Directory.GetFiles(_xslBasePath, pattern, SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        _logger.LogInformation("Arquivo XSL encontrado: {Path}", files[0]);
                        return files[0];
                    }
                }

                // Fallback: procurar qualquer arquivo XSL relacionado
                var allXslFiles = Directory.GetFiles(_xslBasePath, "*.xsl", SearchOption.AllDirectories);
                if (allXslFiles.Length > 0)
                {
                    _logger.LogWarning("Usando arquivo XSL genérico: {Path}", allXslFiles[0]);
                    return allXslFiles[0];
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao procurar arquivo XSL");
                return null;
            }
        }
    }

    /// <summary>
    /// Resultado do pipeline de transformação
    /// </summary>
    public class TransformationPipelineResult
    {
        public bool Success { get; set; }
        public string TransformedXml { get; set; }
        public string TclPath { get; set; }
        public string XslPath { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public Dictionary<string, string> StepResults { get; set; } = new();
        public Dictionary<int, string> SegmentMappings { get; set; } = new();
    }
}

