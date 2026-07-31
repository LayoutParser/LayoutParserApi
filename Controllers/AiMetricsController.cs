using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

using Serilog.Context;

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
        /// Lista paginada de gerações individuais do job de métricas de IA (uma linha
        /// "Geracao concluida." por item), mais recente primeiro. Todos os filtros são opcionais —
        /// sem filtro, retorna a página mais recente.
        /// </summary>
        /// <param name="filter">Filtros de página/tamanho, layout, modelo, sucesso e período (de/ate).</param>
        /// <returns>Página de gerações (<see cref="PagedAiMetricsGenerationsResult"/>).</returns>
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
        /// Resumo agregado das gerações do job de métricas de IA, para os cards do topo do
        /// painel (totais, médias e quebra por tipo de documento). Sem filtro, agrega tudo que
        /// existir no log.
        /// </summary>
        /// <param name="de">Início do período (opcional, inclusivo).</param>
        /// <param name="ate">Fim do período (opcional, inclusivo).</param>
        /// <returns>Resumo agregado (<see cref="AiMetricsSummary"/>).</returns>
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

        /// <summary>
        /// Ingestão do resultado de uma rodada do Cypress em modo batch (fora do processo da API,
        /// na mesma VM do job de métricas) validando um candidato gerado pela IA contra o
        /// Pollux/SEFAZ-fake. Não reescreve o log físico (append-only): grava uma NOVA entrada
        /// Serilog "Cypress validado." (Source=AiMetrics), que o <see cref="IAiMetricsReaderService"/>
        /// faz merge por cima da geração original (que nasceu com CypressValidado/CStatPollux nulos)
        /// ao montar a resposta de GET /api/ai-metrics/generations. Ver adendo (2026-07-30) em
        /// docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md.
        /// </summary>
        /// <param name="request">Layout identificando a geração, resultado da validação e cStat retornado pelo Pollux.</param>
        [HttpPost("cypress-result")]
        public IActionResult PostCypressResult([FromBody] AiMetricsCypressResultRequest? request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Layout))
                return BadRequest(new { success = false, error = "Campo 'layout' é obrigatório." });

            // ✅ Idempotente por natureza (merge lógico na leitura, ver AiMetricsReaderService) —
            // não valida se o layout já existe no histórico; se não existir, o merge simplesmente
            // não casa com nada, sem erro (ver dotnet-standards.md — resiliência).
            try
            {
                using (LogContext.PushProperty("Source", "AiMetrics"))
                {
                    _logger.LogInformation(
                        "Cypress validado. Layout={Layout} CypressValidado={CypressValidado} CStatPollux={CStatPollux} Observacao={Observacao}",
                        request.Layout, request.CypressValidado, request.CStatPollux, request.Observacao);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gravar resultado do Cypress para o layout {Layout}", request.Layout);
                return StatusCode(500, new { success = false, error = "Erro ao gravar resultado do Cypress." });
            }

            return Ok(new { success = true });
        }
    }
}
