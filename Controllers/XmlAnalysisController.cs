using System.Xml.Linq;

using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;

using XslSynth.Model;

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
        private readonly ReverseReconstructionService _reverseReconstructionService;
        private readonly ILayoutParserService _layoutParser;
        private readonly ILogger<XmlAnalysisController> _logger;

        public XmlAnalysisController(
            XmlAnalysisService xmlAnalysisService,
            XsdValidationService xsdValidationService,
            ReverseReconstructionService reverseReconstructionService,
            ILayoutParserService layoutParser,
            ILogger<XmlAnalysisController> logger)
        {
            _xmlAnalysisService = xmlAnalysisService;
            _xsdValidationService = xsdValidationService;
            _reverseReconstructionService = reverseReconstructionService;
            _layoutParser = layoutParser;
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

        /// <summary>
        /// Reconstrução reversa best-effort XML→TXT (issue #151, Fase 4). Reaproveita o mesmo
        /// crosswalk <c>FieldToXmlMapping[]</c> já usado no pathway direto (ex.: <c>fieldMappings</c>
        /// de <see cref="TransformationExecutionController.ExecuteTransformationCandidates"/>) —
        /// nunca reinventa o mapeamento. Ver <see cref="ReverseReconstructionService"/> para o
        /// contrato completo (best-effort, nunca lança, escopo MVP só TXT posicional fixo).
        /// </summary>
        /// <remarks>
        /// Quando <see cref="ReverseReconstructionRequest.OriginalTxt"/> é informado (TXT original
        /// disponível na sessão), a resposta inclui <c>validation</c>: comparação campo a campo entre
        /// o TXT reconstruído e o TXT real (via <see cref="ReverseReconstructionValidator"/>, mesmo
        /// parser posicional do pathway direto). Sem ele, a resposta traz só a reconstrução gerada,
        /// sem seção de validação — nunca finge uma comparação que não rodou.
        /// </remarks>
        /// <response code="200">Reconstrução concluída (best-effort — sempre ver <c>warnings</c>).</response>
        /// <response code="400"><c>LayoutXml</c>/<c>FieldMappings</c>/<c>TargetXml</c> ausente, ou <c>LayoutXml</c>/<c>TargetXml</c> não parseável.</response>
        /// <response code="500">Falha não catalogada.</response>
        [HttpPost("reverse-reconstruct")]
        public async Task<IActionResult> ReverseReconstruct([FromBody] ReverseReconstructionRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.LayoutXml))
                return BadRequest(new { error = "LayoutXml é obrigatório" });

            if (request.FieldMappings == null || request.FieldMappings.Count == 0)
                return BadRequest(new { error = "FieldMappings é obrigatório" });

            if (string.IsNullOrWhiteSpace(request.TargetXml))
                return BadRequest(new { error = "TargetXml é obrigatório" });

            try
            {
                Layout layout;
                using (var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.LayoutXml)))
                {
                    layout = await XmlLayoutLoader.LoadLayoutFromXmlAsync(layoutStream);
                }
                if (layout == null)
                    return BadRequest(new { error = "LayoutXml inválido — não foi possível parsear o layout" });

                XDocument targetXml;
                try
                {
                    targetXml = XDocument.Parse(request.TargetXml);
                }
                catch (Exception ex)
                {
                    return BadRequest(new { error = $"TargetXml inválido: {ex.Message}" });
                }

                // Escopo MVP (ReverseReconstructionService, remarks da classe): só TXT posicional
                // fixo. Linha variável (WithBreakLines == false, ex.: MQSeries/IDOC) fica fora —
                // sinalizado como warning claro, não bloqueado (nunca 400 por causa disso).
                var scopeWarnings = new List<string>();
                if (layout.WithBreakLines == false)
                {
                    scopeWarnings.Add(
                        "Layout com WithBreakLines=false (linha variável, ex.: MQSeries/IDOC) está fora do escopo MVP da reconstrução reversa (issue #151) — resultado é best-effort e tende a ficar incompleto/impreciso.");
                }

                var result = _reverseReconstructionService.Reconstruct(layout, request.FieldMappings, targetXml);

                ReconstructionValidationResult validation = null;
                if (!string.IsNullOrWhiteSpace(request.OriginalTxt))
                {
                    using var layoutStreamForParse = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.LayoutXml));
                    using var txtStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.OriginalTxt));
                    var parsingResult = await _layoutParser.ParseAsync(layoutStreamForParse, txtStream);

                    if (parsingResult.Success && parsingResult.ParsedFields != null)
                    {
                        validation = ReverseReconstructionValidator.Validate(result.ReconstructedFields, parsingResult.ParsedFields);
                    }
                    else
                    {
                        scopeWarnings.Add("OriginalTxt informado, mas o parse contra o layout falhou — validação não pôde ser calculada (ver ParsingResult.ErrorMessage no log do servidor).");
                        _logger.LogWarning(
                            "Reconstrução reversa: OriginalTxt informado mas parse falhou (issue #151). ErrorMessage={ErrorMessage}",
                            parsingResult.ErrorMessage);
                    }
                }

                return Ok(new ReverseReconstructionResponse
                {
                    Success = true,
                    ReconstructedText = result.ReconstructedText,
                    FieldsAttempted = result.FieldsAttempted,
                    FieldsReconstructed = result.FieldsReconstructed,
                    Warnings = result.Warnings,
                    ScopeWarnings = scopeWarnings,
                    Validation = validation
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro na reconstrução reversa XML->TXT (issue #151)");
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

    /// <summary>Requisição de reconstrução reversa best-effort XML→TXT (issue #151, Fase 4).</summary>
    public class ReverseReconstructionRequest
    {
        /// <summary>XML do layout de origem (mesmo conteúdo aceito por <c>AnalyzeXml.LayoutXml</c>) —
        /// não é o LayoutGuid do catálogo, é o XML já decifrado/resolvido pelo chamador.</summary>
        public string LayoutXml { get; set; }

        /// <summary>Crosswalk já resolvido — o mesmo <c>FieldToXmlMapping[]</c> composto por
        /// <see cref="Services.Transformation.StructuralResolution.FieldMappingCompositionService"/>
        /// no pathway direto (ex.: <c>candidates[].fieldMappings</c> de
        /// <c>execute-candidates</c>). Este endpoint não recompõe o mapeamento — só reconstrói.</summary>
        public List<FieldToXmlMapping> FieldMappings { get; set; }

        /// <summary>XML de destino (já transformado) a partir do qual reconstruir o TXT.</summary>
        public string TargetXml { get; set; }

        /// <summary>
        /// Opcional — TXT original, quando disponível na sessão. Se informado, a resposta inclui
        /// <c>validation</c> (comparação campo a campo contra o TXT reconstruído). Ausente/vazio:
        /// resposta traz só a reconstrução, sem seção de validação.
        /// </summary>
        public string OriginalTxt { get; set; }
    }

    /// <summary>Resposta do <c>POST reverse-reconstruct</c> (issue #151, Fase 4). Tipo público (não
    /// anônimo) de propósito — permite ao chamador (front-end/testes) desserializar/inspecionar sem
    /// depender de reflexão sobre tipo anônimo.</summary>
    public class ReverseReconstructionResponse
    {
        public bool Success { get; set; }
        public string ReconstructedText { get; set; } = string.Empty;
        public int FieldsAttempted { get; set; }
        public int FieldsReconstructed { get; set; }
        public List<ReconstructionWarning> Warnings { get; set; } = new();

        /// <summary>Avisos de escopo (ex.: layout fora do MVP, falha ao parsear <c>OriginalTxt</c>) —
        /// distintos de <see cref="Warnings"/> (que vêm de <see cref="ReverseReconstructionService"/>
        /// por campo).</summary>
        public List<string> ScopeWarnings { get; set; } = new();

        /// <summary><c>null</c> quando <c>OriginalTxt</c> não foi informado ou o parse falhou — nunca
        /// finge uma validação que não rodou (issue #151, item 3 do critério de aceite).</summary>
        public ReconstructionValidationResult? Validation { get; set; }
    }
}
