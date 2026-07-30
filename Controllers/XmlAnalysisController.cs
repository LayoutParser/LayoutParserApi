using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class XmlAnalysisController : ControllerBase
    {
        private readonly XmlAnalysisService _xmlAnalysisService;
        private readonly XsdValidationService _xsdValidationService;
        private readonly ILogger<XmlAnalysisController> _logger;

        public XmlAnalysisController(
            XmlAnalysisService xmlAnalysisService,
            XsdValidationService xsdValidationService,
            ILogger<XmlAnalysisController> logger)
        {
            _xmlAnalysisService = xmlAnalysisService;
            _xsdValidationService = xsdValidationService;
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
                    result.Orientations = orientations;
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

        // Endpoint "analyze-xsd-error-with-ai" (baseado em GeminiAIService) foi removido:
        // Gemini foi decomissionado (ver .claude/agent-memory/lp-backend-dev/generation-services-unregistered-di.md)
        // e o caso de uso equivalente já existe via Ollama em ValidationDiagnosticController
        // (POST /api/xml-analysis/diagnose-validation-error).

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

    public class XmlTransformRequest
    {
        public string XmlContent { get; set; }
    }
}
