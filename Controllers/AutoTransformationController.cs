using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LayoutParserApi.Services.XmlAnalysis;
using System.Threading.Tasks;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AutoTransformationController : ControllerBase
    {
        private readonly ILogger<AutoTransformationController> _logger;
        private readonly AutoTransformationGeneratorService _autoGenerator;

        public AutoTransformationController(
            ILogger<AutoTransformationController> logger,
            AutoTransformationGeneratorService autoGenerator)
        {
            _logger = logger;
            _autoGenerator = autoGenerator;
        }

        /// <summary>
        /// Gera automaticamente TCL e XSL para todos os layouts
        /// </summary>
        [HttpPost("generate-all")]
        public async Task<IActionResult> GenerateAllTransformations()
        {
            try
            {
                _logger.LogInformation("Iniciando geração automática de TCL e XSL");

                var result = await _autoGenerator.GenerateAllTransformationsAsync();

                return Ok(new
                {
                    success = result.Success,
                    processedCount = result.ProcessedLayouts.Count,
                    successCount = result.ProcessedLayouts.Count(p => p.Success),
                    errorCount = result.Errors.Count,
                    warningCount = result.Warnings.Count,
                    processedLayouts = result.ProcessedLayouts.Select(p => new
                    {
                        layoutGuid = p.LayoutGuid,
                        layoutName = p.LayoutName,
                        layoutType = p.LayoutType,
                        success = p.Success,
                        generatedFiles = p.GeneratedFiles,
                        errors = p.Errors,
                        warnings = p.Warnings
                    }),
                    errors = result.Errors,
                    warnings = result.Warnings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar transformações automaticamente");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gera TCL e XSL para um layout específico
        /// </summary>
        [HttpPost("generate-for-layout")]
        public async Task<IActionResult> GenerateForLayout([FromBody] GenerateForLayoutRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.LayoutGuid) && string.IsNullOrEmpty(request.LayoutName))
                {
                    return BadRequest("LayoutGuid ou LayoutName é obrigatório");
                }

                _logger.LogInformation("Gerando transformações para layout: {LayoutGuid} / {LayoutName}", 
                    request.LayoutGuid, request.LayoutName);

                // Buscar layout do Redis
                var layoutsRequest = new Models.Requests.LayoutSearchRequest
                {
                    PageNumber = 1,
                    PageSize = 100
                };

                if (!string.IsNullOrEmpty(request.LayoutGuid))
                {
                    layoutsRequest.LayoutGuid = request.LayoutGuid;
                }

                if (!string.IsNullOrEmpty(request.LayoutName))
                {
                    layoutsRequest.LayoutName = request.LayoutName;
                }

                var layoutsResponse = await _autoGenerator.GetLayoutDatabaseService()
                    .SearchLayoutsFromDatabase(layoutsRequest);

                if (layoutsResponse == null || !layoutsResponse.Layouts.Any())
                {
                    return NotFound(new { error = "Layout não encontrado" });
                }

                var layout = layoutsResponse.Layouts.First();
                var processed = await _autoGenerator.ProcessLayoutAsync(layout);

                return Ok(new
                {
                    success = processed.Success,
                    layoutGuid = processed.LayoutGuid,
                    layoutName = processed.LayoutName,
                    layoutType = processed.LayoutType,
                    generatedFiles = processed.GeneratedFiles,
                    errors = processed.Errors,
                    warnings = processed.Warnings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar transformações para layout");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class GenerateForLayoutRequest
    {
        public string LayoutGuid { get; set; }
        public string LayoutName { get; set; }
    }
}

