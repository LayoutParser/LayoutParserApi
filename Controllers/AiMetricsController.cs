using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Painel de métricas de geração de IA (Gap 3 —
    /// docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md). Expõe, em JSON tipado,
    /// as linhas "Geracao concluida." (Source=AiMetrics) que o job
    /// ai/XslSynth --mode=metrics-batch já grava no mesmo log lido por GET api/logs — sem exigir
    /// que o front-end faça parsing de texto.
    /// Rota explícita (não [controller]) porque o contrato do handoff fixa "/api/ai-metrics".
    /// </summary>
    [ApiController]
    [Route("api/ai-metrics")]
    public class AiMetricsController : ControllerBase
    {
        private readonly IAiMetricsReaderService _aiMetricsReaderService;
        private readonly ILogger<AiMetricsController> _logger;

        public AiMetricsController(IAiMetricsReaderService aiMetricsReaderService, ILogger<AiMetricsController> logger)
        {
            _aiMetricsReaderService = aiMetricsReaderService;
            _logger = logger;
        }

        /// <summary>
        /// Lista paginada de gerações individuais, mais recente primeiro.
        /// </summary>
        [HttpGet("generations")]
        public async Task<IActionResult> GetGenerations([FromQuery] AiMetricsGenerationFilter filter)
        {
            try
            {
                var result = await _aiMetricsReaderService.GetGenerationsAsync(filter);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao consultar gerações de métricas de IA");
                return StatusCode(500, new { success = false, error = "Erro ao consultar métricas de IA." });
            }
        }

        /// <summary>
        /// Resumo agregado para os cards do topo do painel.
        /// </summary>
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary([FromQuery] DateTime? de, [FromQuery] DateTime? ate)
        {
            try
            {
                var result = await _aiMetricsReaderService.GetSummaryAsync(de, ate);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao consultar resumo de métricas de IA");
                return StatusCode(500, new { success = false, error = "Erro ao consultar resumo de métricas de IA." });
            }
        }
    }
}
