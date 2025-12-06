using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MonitoringController : ControllerBase
    {
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly ILayoutParserService _parserService;
        private readonly ILogger<MonitoringController> _logger;

        public MonitoringController(
            ICachedLayoutService cachedLayoutService,
            ILayoutParserService parserService,
            ILogger<MonitoringController> logger)
        {
            _cachedLayoutService = cachedLayoutService;
            _parserService = parserService;
            _logger = logger;
        }

        /// <summary>
        /// Retorna análise completa de todos os layouts com validações e cálculos
        /// </summary>
        [HttpGet("layouts-analysis")]
        public async Task<IActionResult> GetLayoutsAnalysis()
        {
            try
            {
                _logger.LogInformation("Iniciando análise completa de todos os layouts");

                // Buscar todos os layouts
                var request = new LayoutSearchRequest
                {
                    SearchTerm = "", // String vazia = buscar todos
                    MaxResults = 1000
                };

                var layoutsResponse = await _cachedLayoutService.SearchLayoutsAsync(request);

                if (!layoutsResponse.Success || layoutsResponse.Layouts == null || !layoutsResponse.Layouts.Any())
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Nenhum layout encontrado",
                        totalLayouts = 0,
                        layouts = new List<object>()
                    });
                }

                var analysisResults = new List<object>();
                int totalLayouts = layoutsResponse.Layouts.Count;
                int validLayouts = 0;
                int invalidLayouts = 0;
                int layoutsWithErrors = 0;

                foreach (var layoutRecord in layoutsResponse.Layouts)
                {
                    try
                    {
                        // Carregar layout do XML descriptografado
                        if (string.IsNullOrEmpty(layoutRecord.DecryptedContent))
                        {
                            analysisResults.Add(new
                            {
                                layoutGuid = layoutRecord.LayoutGuid,
                                name = layoutRecord.Name,
                                status = "error",
                                error = "Layout sem conteúdo descriptografado",
                                lineValidations = new List<object>()
                            });
                            layoutsWithErrors++;
                            continue;
                        }

                        // Parsear XML do layout
                        var layout = await _parserService.ParseLayoutFromXmlAsync(layoutRecord.DecryptedContent);
                        if (layout == null)
                        {
                            analysisResults.Add(new
                            {
                                layoutGuid = layoutRecord.LayoutGuid,
                                name = layoutRecord.Name,
                                status = "error",
                                error = "Erro ao parsear XML do layout",
                                lineValidations = new List<object>()
                            });
                            layoutsWithErrors++;
                            continue;
                        }

                        // Verificar se o layout deve ter cálculo de validação
                        var expectedLineLength = LayoutLineSizeConfiguration.GetLineSizeForLayout(layoutRecord.LayoutGuid);
                        
                        List<LineValidationInfo>? lineValidations = null;
                        int validLines = 0;
                        int invalidLines = 0;
                        int totalLines = 0;
                        bool isLayoutValid = false;

                        if (expectedLineLength.HasValue)
                        {
                            // Calcular validações apenas para layouts configurados
                            lineValidations = _parserService.CalculateLineValidations(layout, expectedLineLength.Value);

                            // Contar linhas válidas e inválidas
                            validLines = lineValidations.Count(lv => lv.IsValid);
                            invalidLines = lineValidations.Count(lv => !lv.IsValid);
                            totalLines = lineValidations.Count;

                            isLayoutValid = invalidLines == 0 && totalLines > 0;
                            if (isLayoutValid) validLayouts++;
                            else invalidLayouts++;
                        }
                        else
                        {
                            // Layout não configurado para cálculo
                            lineValidations = new List<LineValidationInfo>();
                        }

                        analysisResults.Add(new
                        {
                            layoutGuid = layoutRecord.LayoutGuid,
                            name = layoutRecord.Name,
                            description = layoutRecord.Description,
                            layoutType = layoutRecord.LayoutType,
                            status = expectedLineLength.HasValue 
                                ? (isLayoutValid ? "valid" : "invalid") 
                                : "not_configured",
                            expectedLineLength = expectedLineLength,
                            totalLines = totalLines,
                            validLines = validLines,
                            invalidLines = invalidLines,
                            linesWithChildren = lineValidations?.Count(lv => lv.HasChildren) ?? 0,
                            lineValidations = lineValidations?.Select(lv => new
                            {
                                lineName = lv.LineName,
                                initialValue = lv.InitialValue,
                                initialValueLength = lv.InitialValueLength,
                                sequenceFromPreviousLine = lv.SequenceFromPreviousLine,
                                fieldsLength = lv.FieldsLength,
                                sequenciaLength = lv.SequenciaLength,
                                totalLength = lv.TotalLength,
                                isValid = lv.IsValid,
                                hasChildren = lv.HasChildren,
                                fieldCount = lv.FieldCount,
                                calculatedPositions = lv.CalculatedPositions
                            }) ?? new List<object>()
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao analisar layout {LayoutName}", layoutRecord.Name);
                        analysisResults.Add(new
                        {
                            layoutGuid = layoutRecord.LayoutGuid,
                            name = layoutRecord.Name,
                            status = "error",
                            error = ex.Message,
                            lineValidations = new List<object>()
                        });
                        layoutsWithErrors++;
                    }
                }

                return Ok(new
                {
                    success = true,
                    timestamp = DateTime.UtcNow,
                    summary = new
                    {
                        totalLayouts = totalLayouts,
                        validLayouts = validLayouts,
                        invalidLayouts = invalidLayouts,
                        layoutsWithErrors = layoutsWithErrors,
                        validationRate = totalLayouts > 0 ? (double)validLayouts / totalLayouts * 100 : 0
                    },
                    layouts = analysisResults
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar análise de layouts");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }
    }
}

