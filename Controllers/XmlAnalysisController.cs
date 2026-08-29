using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Análise estrutural e validação de XML — contagem de elementos/profundidade, validação
    /// contra XSD da SEFAZ (com detecção automática de tipo/versão) e transformações utilitárias
    /// de NFe. Ver <see cref="ValidationDiagnosticController"/> (mesma rota base
    /// <c>api/xml-analysis</c>, controller separado) para diagnóstico via Ollama.
    /// </summary>
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

        /// <summary>Analisa a estrutura de um XML (elementos, atributos, profundidade) e, se <c>LayoutXml</c> for informado, valida contra o layout.</summary>
        /// <response code="200">Análise concluída.</response>
        /// <response code="400"><c>XmlContent</c> ausente.</response>
        /// <response code="500">Falha não catalogada.</response>
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

        /// <summary>Mesma análise de <see cref="AnalyzeXml"/>, mas recebendo o XML (e opcionalmente o layout) como upload multipart em vez de JSON.</summary>
        /// <response code="200">Análise concluída.</response>
        /// <response code="400"><c>xmlFile</c> ausente ou não é <c>.xml</c>.</response>
        /// <response code="500">Falha não catalogada.</response>
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
        /// Valida XML contra o XSD oficial da SEFAZ. Detecta automaticamente tipo de documento
        /// e versão do schema (<c>XsdVersion</c>/<c>LayoutName</c> são dicas opcionais, não
        /// obrigatórias). Em caso de erro, já anexa <c>orientations</c> (texto de orientação por
        /// código de erro) para poupar uma segunda chamada a <see cref="GetOrientations"/>.
        /// </summary>
        /// <response code="200">Validação concluída (ver <c>isValid</c> — XML inválido não é erro HTTP).</response>
        /// <response code="400"><c>XmlContent</c> ausente.</response>
        /// <response code="500">Falha não catalogada.</response>
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

        // Endpoint "analyze-xsd-error-with-ai" (baseado no antigo GeminiAIService, removido do
        // repositório em 2026-08-10 junto com o decommission de Gemini/OpenAI) foi removido:
        // Gemini foi decomissionado (ver .claude/agent-memory/lp-backend-dev/generation-services-unregistered-di.md)
        // e o caso de uso equivalente já existe via Ollama em ValidationDiagnosticController
        // (POST /api/xml-analysis/diagnose-validation-error).

        /// <summary>Utilitário de normalização de XML NFe: remove o envelope <c>enviNFe</c> e garante o namespace correto — não é o pathway de transformação principal (TXT→XML).</summary>
        /// <response code="200">XML transformado.</response>
        /// <response code="400"><c>XmlContent</c> ausente.</response>
        /// <response code="500">Falha não catalogada.</response>
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

        /// <summary>Consulta o texto de orientação de correção para um conjunto de códigos de erro XSD, sem precisar rodar a validação de novo.</summary>
        /// <param name="xsdVersion">Versão do schema NFe (default <c>PL_010b_NT2025_002_v1.30</c>).</param>
        /// <param name="errorCodes">Códigos de erro a explicar (opcional — vazio devolve todas as orientações conhecidas da versão).</param>
        /// <response code="200">Orientações encontradas.</response>
        /// <response code="500">Falha não catalogada.</response>
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

    /// <summary>Requisição de análise estrutural de XML.</summary>
    public class XmlAnalysisRequest
    {
        public string XmlContent { get; set; }
        /// <summary>Layout XML opcional — se informado, a análise valida o XML contra ele.</summary>
        public string LayoutXml { get; set; }
    }

    /// <summary>Requisição de validação contra XSD da SEFAZ.</summary>
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
