using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.XmlAnalysis;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.Generation.Implementations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class XmlAnalysisController : ControllerBase
    {
        private readonly XmlAnalysisService _xmlAnalysisService;
        private readonly XsdValidationService _xsdValidationService;
        private readonly GeminiAIService _geminiAIService;
        private readonly ILogger<XmlAnalysisController> _logger;

        public XmlAnalysisController(
            XmlAnalysisService xmlAnalysisService,
            XsdValidationService xsdValidationService,
            GeminiAIService geminiAIService,
            ILogger<XmlAnalysisController> logger)
        {
            _xmlAnalysisService = xmlAnalysisService;
            _xsdValidationService = xsdValidationService;
            _geminiAIService = geminiAIService;
            _logger = logger;
        }

        /// <summary>
        /// Analisa e valida um arquivo XML
        /// </summary>
        [HttpPost("analyze")]
        public async Task<IActionResult> AnalyzeXml([FromBody] XmlAnalysisRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.XmlContent))
            {
                return BadRequest("Conteúdo XML é obrigatório");
            }

            try
            {
                _logger.LogInformation("Iniciando análise XML");

                // Carregar layout se fornecido
                Layout layout = null;
                if (!string.IsNullOrEmpty(request.LayoutXml))
                {
                    using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.LayoutXml));
                    layout = await XmlLayoutLoader.LoadLayoutFromXmlAsync(layoutStream);
                }

                // Analisar XML
                var result = await _xmlAnalysisService.AnalyzeXmlAsync(request.XmlContent, layout);

                return Ok(new
                {
                    success = result.Success,
                    errors = result.Errors,
                    warnings = result.Warnings,
                    totalElements = result.TotalElements,
                    totalAttributes = result.TotalAttributes,
                    depth = result.Depth,
                    validationDetails = result.ValidationDetails
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar XML");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Valida XML enviado como arquivo
        /// </summary>
        [HttpPost("validate-file")]
        public async Task<IActionResult> ValidateXmlFile(IFormFile xmlFile, IFormFile layoutFile = null)
        {
            if (xmlFile == null)
                return BadRequest("Arquivo XML é obrigatório");

            if (Path.GetExtension(xmlFile.FileName).ToLower() != ".xml")
                return BadRequest("O arquivo deve ser XML");

            try
            {
                string xmlContent;
                using (var reader = new StreamReader(xmlFile.OpenReadStream()))
                {
                    xmlContent = await reader.ReadToEndAsync();
                }

                Layout layout = null;
                if (layoutFile != null)
                {
                    using var layoutStream = layoutFile.OpenReadStream();
                    layout = await XmlLayoutLoader.LoadLayoutFromXmlAsync(layoutStream);
                }

                var result = await _xmlAnalysisService.AnalyzeXmlAsync(xmlContent, layout);

                return Ok(new
                {
                    success = result.Success,
                    errors = result.Errors,
                    warnings = result.Warnings,
                    totalElements = result.TotalElements,
                    totalAttributes = result.TotalAttributes,
                    depth = result.Depth,
                    validationDetails = result.ValidationDetails
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar arquivo XML");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Valida XML contra XSD da SEFAZ (detecta automaticamente o tipo de documento)
        /// </summary>
        [HttpPost("validate-xsd")]
        public async Task<IActionResult> ValidateXsd([FromBody] XsdValidationRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.XmlContent))
            {
                return BadRequest("Conteúdo XML é obrigatório");
            }

            try
            {
                _logger.LogInformation("Iniciando validação XSD (detecção automática de tipo)");

                // Validar com detecção automática (xsdVersion e layoutName são opcionais)
                var result = await _xsdValidationService.ValidateXmlAgainstXsdAsync(
                    request.XmlContent, 
                    request.XsdVersion, 
                    request.LayoutName);

                // Se houver erros, obter orientações
                if (!result.IsValid && result.Errors.Any())
                {
                    var errorCodes = result.Errors.Select(e => e.Message).ToList();
                    var orientations = await _xsdValidationService.GetOrientationsAsync(result.XsdVersion, errorCodes);
                    result.Orientations = orientations.Orientations;
                }

                return Ok(new
                {
                    success = result.IsValid,
                    isValid = result.IsValid,
                    documentType = result.DocumentType,
                    xsdVersion = result.XsdVersion,
                    errors = result.Errors,
                    warnings = result.Warnings,
                    orientations = result.Orientations,
                    transformedXml = result.TransformedXml
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar XML contra XSD");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Analisa erro XSD com IA para ajudar o usuário (com suporte a MQSeries/IDOC)
        /// </summary>
        [HttpPost("analyze-xsd-error-with-ai")]
        public async Task<IActionResult> AnalyzeXsdErrorWithAi([FromBody] XsdErrorAnalysisRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.XsdError))
            {
                return BadRequest("Erro XSD é obrigatório");
            }

            try
            {
                _logger.LogInformation("Analisando erro XSD com IA. Tipo documento: {DocumentType}", request.DocumentType);

                // Se for MQSeries/IDOC, transformar primeiro e mapear erros
                string originalContent = request.OriginalContent ?? request.XmlContent;
                string transformedXml = request.XmlContent;
                Dictionary<int, string> segmentMappings = null;

                if (request.DocumentType == "MQSeries" || request.DocumentType == "IDOC")
                {
                    // Transformar MQSeries/IDOC para XML usando pipeline
                    var transformer = HttpContext.RequestServices.GetRequiredService<MqSeriesToXmlTransformer>();

                    var transformationResult = await transformer.TransformToXmlAsync(
                        originalContent, 
                        request.LayoutName ?? "");

                    if (transformationResult.Success && !string.IsNullOrEmpty(transformationResult.TransformedXml))
                    {
                        transformedXml = transformationResult.TransformedXml;
                        segmentMappings = transformationResult.SegmentMappings
                            .ToDictionary(m => m.Key, m => m.Value.MqSeriesSegment);
                    }
                }
                else if (request.DocumentType == "XML")
                {
                    // Para XML→XML, usar pipeline de transformação XML→XML
                    var pipelineService = HttpContext.RequestServices.GetRequiredService<TransformationPipelineService>();
                    
                    // Detectar tipo de documento de origem e destino
                    var docTypeDetector = HttpContext.RequestServices.GetRequiredService<XmlDocumentTypeDetector>();
                    var sourceDocType = docTypeDetector.DetectDocumentType(originalContent);
                    var targetDocType = "NFe"; // Pode ser melhorado para detectar automaticamente

                    var pipelineResult = await pipelineService.TransformXmlToXmlAsync(
                        originalContent,
                        sourceDocType.Type,
                        targetDocType,
                        request.LayoutName);

                    if (pipelineResult.Success && !string.IsNullOrEmpty(pipelineResult.TransformedXml))
                    {
                        transformedXml = pipelineResult.TransformedXml;
                    }
                }

                // Construir prompt para a IA
                var prompt = BuildAiPrompt(request, transformedXml, originalContent, segmentMappings);

                var aiResponse = await _geminiAIService.CallGeminiAPI(prompt);

                // Mapear erro XML de volta para segmento MQSeries se aplicável
                var mqSeriesSegment = MapXmlErrorToMqSeriesSegment(request.XsdError, segmentMappings);

                return Ok(new
                {
                    success = true,
                    analysis = aiResponse,
                    error = request.XsdError,
                    fieldName = request.FieldName,
                    mqSeriesSegment = mqSeriesSegment,
                    documentType = request.DocumentType
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar erro XSD com IA");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Constrói prompt para IA baseado no tipo de documento
        /// </summary>
        private string BuildAiPrompt(XsdErrorAnalysisRequest request, string transformedXml, string originalContent, Dictionary<int, string> segmentMappings)
        {
            if (request.DocumentType == "MQSeries" || request.DocumentType == "IDOC")
            {
                return $@"Você é um especialista em validação de documentos fiscais MQSeries/IDOC e XML NFe da SEFAZ.

CONTEXTO:
O documento original é {request.DocumentType} que foi transformado para XML NFe para validação XSD.

ERRO XSD ENCONTRADO:
{request.XsdError}

DOCUMENTO ORIGINAL ({request.DocumentType}):
{originalContent.Substring(0, Math.Min(3000, originalContent.Length))}

XML TRANSFORMADO (para referência):
{transformedXml.Substring(0, Math.Min(2000, transformedXml.Length))}

CAMPO AFETADO: {request.FieldName ?? "Não especificado"}

IMPORTANTE: Você deve identificar em qual SEGMENTO do layout MQSeries/IDOC original está o problema.

Por favor, forneça:
1. Uma explicação clara do erro
2. Em qual SEGMENTO do layout MQSeries/IDOC está o problema (ex: HEADER, LINHA000, LINHA001, etc.)
3. Qual campo específico no segmento precisa ser corrigido
4. O que causou este erro
5. Como corrigir o problema no documento original
6. Um exemplo de valor correto (se aplicável)

Seja objetivo e direto, focando na solução prática e sempre referenciando o SEGMENTO do layout original.";
            }
            else
            {
                // XML direto
                return $@"Você é um especialista em validação de XML NFe da SEFAZ. 

Um erro foi encontrado durante a validação XSD:

ERRO: {request.XsdError}

CONTEXTO DO XML:
{transformedXml.Substring(0, Math.Min(2000, transformedXml.Length))}

CAMPO AFETADO: {request.FieldName ?? "Não especificado"}

Por favor, forneça:
1. Uma explicação clara e simples do erro
2. O que causou este erro
3. Como corrigir o problema
4. Um exemplo de valor correto (se aplicável)

Seja objetivo e direto, focando na solução prática.";
            }
        }

        /// <summary>
        /// Mapeia erro XML de volta para segmento MQSeries
        /// </summary>
        private string MapXmlErrorToMqSeriesSegment(string xsdError, Dictionary<int, string> segmentMappings)
        {
            if (segmentMappings == null || segmentMappings.Count == 0)
                return null;

            // Tentar extrair informação do erro que indique elemento XML
            // Exemplo: "Falha da analise do elemento '{http://www.portalfiscal.inf.br/nfe}ide'"
            var elementMatch = System.Text.RegularExpressions.Regex.Match(
                xsdError, 
                @"elemento\s+['{]([^'{}]+)['}]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (elementMatch.Success)
            {
                var xmlElement = elementMatch.Groups[1].Value.Split(':').Last();
                
                // Tentar encontrar mapeamento (lógica simplificada - pode ser melhorada)
                // Por enquanto, retornar o primeiro segmento que contém o elemento
                return segmentMappings.Values.FirstOrDefault();
            }

            return null;
        }

        /// <summary>
        /// Transforma XML NFe (remove enviNFe, adiciona namespace)
        /// </summary>
        [HttpPost("transform-nfe")]
        public IActionResult TransformNFe([FromBody] XmlTransformRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.XmlContent))
            {
                return BadRequest("Conteúdo XML é obrigatório");
            }

            try
            {
                var transformed = _xsdValidationService.TransformNFeXml(request.XmlContent);
                return Ok(new
                {
                    success = true,
                    transformedXml = transformed
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar XML NFe");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Obtém orientações para correção de erros XSD
        /// </summary>
        [HttpGet("orientations")]
        public async Task<IActionResult> GetOrientations([FromQuery] string xsdVersion = "PL_010b_NT2025_002_v1.30", [FromQuery] string[] errorCodes = null)
        {
            try
            {
                var errorCodesList = errorCodes?.ToList();
                var result = await _xsdValidationService.GetOrientationsAsync(xsdVersion, errorCodesList);
                
                return Ok(new
                {
                    success = result.Success,
                    orientations = result.Orientations
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter orientações");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class XmlAnalysisRequest
    {
        public string XmlContent { get; set; }
        public string LayoutXml { get; set; }
    }

    public class XsdValidationRequest
    {
        public string XmlContent { get; set; }
        public string XsdVersion { get; set; } // Opcional - será detectado automaticamente se não fornecido
        public string LayoutName { get; set; } // Opcional - ajuda na detecção do tipo de documento
    }

    public class XsdErrorAnalysisRequest
    {
        public string XsdError { get; set; }
        public string XmlContent { get; set; }
        public string FieldName { get; set; }
    }

    public class XmlTransformRequest
    {
        public string XmlContent { get; set; }
    }
}
