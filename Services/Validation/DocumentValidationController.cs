using LayoutParserApi.Models.Validation;
using LayoutParserApi.Services.Validation;
using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Controller apenas para validação de documentos em tempo real (quando usuário faz upload)
    /// Validação de layouts é feita automaticamente pelo Background Service
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentValidationController : ControllerBase
    {
        private readonly DocumentValidationService _documentValidationService;
        private readonly DocumentMLValidationService _mlValidationService;
        private readonly LayoutValidationService _layoutValidationService;
        private readonly ILogger<DocumentValidationController> _logger;

        public DocumentValidationController(
            DocumentValidationService documentValidationService,
            DocumentMLValidationService mlValidationService,
            LayoutValidationService layoutValidationService,
            ILogger<DocumentValidationController> logger)
        {
            _documentValidationService = documentValidationService;
            _mlValidationService = mlValidationService;
            _layoutValidationService = layoutValidationService;
            _logger = logger;
        }

        /// <summary>
        /// Valida um documento TXT em tempo real (chamado durante upload)
        /// </summary>
        [HttpPost("document")]
        public async Task<IActionResult> ValidateDocument([FromBody] DocumentValidationRequest request)
        {
            try
            {
                _logger.LogInformation("Validando documento TXT (tamanho: {Length} chars)", request.DocumentContent?.Length ?? 0);

                if (string.IsNullOrEmpty(request.DocumentContent))
                {
                    return BadRequest(new { error = "Conteúdo do documento é obrigatório" });
                }

                var result = _documentValidationService.ValidateDocument(request.DocumentContent);

                // Se houver erros, gerar sugestões ML
                if (!result.IsValid && result.LineErrors.Any() && !string.IsNullOrEmpty(request.LayoutGuid))
                {
                    var firstError = result.LineErrors.First();
                    var suggestions = await _mlValidationService.AnalyzeErrorAndSuggestAsync(
                        firstError,
                        request.DocumentContent,
                        request.LayoutGuid);

                    return Ok(new
                    {
                        validation = result,
                        suggestions = suggestions
                    });
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar documento");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Obtém resultados de validação de layouts (para exibição no admin)
        /// </summary>
        [HttpGet("layouts/results")]
        public async Task<IActionResult> GetLayoutValidationResults([FromQuery] List<string>? layoutGuids = null)
        {
            try
            {
                List<LayoutValidationResult> results;

                if (layoutGuids != null && layoutGuids.Any())
                {
                    results = new List<LayoutValidationResult>();
                    foreach (var guid in layoutGuids)
                    {
                        var cached = _layoutValidationService.GetCachedValidation(guid);
                        if (cached != null)
                            results.Add(cached);
                        else
                        {
                            // Se não estiver em cache, validar agora
                            var result = await _layoutValidationService.ValidateLayoutByGuidAsync(guid);
                            results.Add(result);
                        }
                    }
                }
                else
                {
                    // Buscar todos os layouts com erro do cache
                    // Nota: O Background Service mantém o cache atualizado
                    results = new List<LayoutValidationResult>();
                    // Aqui você pode implementar um método para obter todos os resultados do cache
                    // Por enquanto, retorna lista vazia e o front-end pode chamar com layoutGuids específicos
                }

                // Filtrar apenas os que têm erros
                var errorsOnly = results.Where(r => !r.IsValid).ToList();

                return Ok(new
                {
                    total = results.Count,
                    withErrors = errorsOnly.Count,
                    results = errorsOnly,
                    message = "Nota: Validações são executadas automaticamente pelo sistema. Use layoutGuids para obter resultados específicos."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter resultados de validação");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Obtém sugestões ML para um erro específico
        /// </summary>
        [HttpPost("document/suggestions")]
        public async Task<IActionResult> GetSuggestions([FromBody] SuggestionRequest request)
        {
            try
            {
                var suggestions = await _mlValidationService.AnalyzeErrorAndSuggestAsync(
                    request.LineError,
                    request.DocumentContent,
                    request.LayoutGuid ?? "");

                return Ok(suggestions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter sugestões");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Registra feedback sobre uma sugestão (para aprendizado ML)
        /// </summary>
        [HttpPost("document/feedback")]
        public async Task<IActionResult> RegisterFeedback([FromBody] SuggestionFeedbackRequest request)
        {
            try
            {
                await _mlValidationService.RegisterFeedbackAsync(
                    request.SuggestionId,
                    request.WasAccepted,
                    request.ActualCorrection);

                return Ok(new { success = true, message = "Feedback registrado com sucesso" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao registrar feedback");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Registra um documento processado para aprendizado ML
        /// </summary>
        [HttpPost("document/learn")]
        public async Task<IActionResult> LearnFromDocument([FromBody] LearnFromDocumentRequest request)
        {
            try
            {
                await _mlValidationService.LearnFromDocumentAsync(
                    request.DocumentContent,
                    request.LayoutGuid,
                    request.Errors);

                return Ok(new { success = true, message = "Documento registrado para aprendizado" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao registrar documento para aprendizado");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    // Request models
    public class DocumentValidationRequest
    {
        public string DocumentContent { get; set; } = "";
        public string? LayoutGuid { get; set; }
    }

    public class SuggestionRequest
    {
        public DocumentLineError LineError { get; set; } = new();
        public string DocumentContent { get; set; } = "";
        public string? LayoutGuid { get; set; }
    }

    public class SuggestionFeedbackRequest
    {
        public string SuggestionId { get; set; } = "";
        public bool WasAccepted { get; set; }
        public string? ActualCorrection { get; set; }
    }

    public class LearnFromDocumentRequest
    {
        public string DocumentContent { get; set; } = "";
        public string LayoutGuid { get; set; } = "";
        public List<DocumentLineError>? Errors { get; set; }
    }
}

