using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Logging;

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
        // Tetos de tamanho dos campos que viram texto de log (ver LogMessageSanitizer). Layout e
        // cStat são curtos por natureza; a observação é texto livre e precisa de um limite explícito.
        private const int MaxLayoutLength = 500;
        private const int MaxCStatLength = 20;
        private const int MaxObservacaoLength = 1000;

        private readonly IAiMetricsReaderService _aiMetricsReaderService;
        private readonly IAiMetricsIngestService _aiMetricsIngestService;
        private readonly ILogger<AiMetricsController> _logger;

        public AiMetricsController(
            IAiMetricsReaderService aiMetricsReaderService,
            IAiMetricsIngestService aiMetricsIngestService,
            ILogger<AiMetricsController> logger)
        {
            _aiMetricsReaderService = aiMetricsReaderService;
            _aiMetricsIngestService = aiMetricsIngestService;
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
        /// <remarks>
        /// Exige o header <c>X-AiMetrics-Key</c> (ver <see cref="AiMetricsIngestKeyFilter"/>): é
        /// escrita que vira número no painel da diretoria, e a app não tem pipeline de autenticação.
        /// </remarks>
        [HttpPost("cypress-result")]
        [ServiceFilter(typeof(AiMetricsIngestKeyFilter))]
        public IActionResult PostCypressResult([FromBody] AiMetricsCypressResultRequest? request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Layout))
                return BadRequest(new { success = false, error = "Campo 'layout' é obrigatório." });

            // ✅ FIX (QA/Quinn 2026-07-30): TODO campo vindo do cliente é higienizado ANTES de virar
            // linha de log — o log é relido como fonte de dados do painel, então uma quebra de linha
            // crua na observação (um stack trace colado basta) injetava uma GERAÇÃO FANTASMA inteira
            // no gráfico, com métricas arbitrárias. Mesma proteção que o LogsController já aplicava
            // na ingestão de log do front-end. O outro vetor — texto com "=" forjando campo — é
            // tratado no parser (regex ancorado, ver AiMetricsReaderService.TryParseCypress).
            var layout = LogMessageSanitizer.Sanitize(request.Layout, MaxLayoutLength);
            var cStatPollux = LogMessageSanitizer.SanitizeOptional(request.CStatPollux, MaxCStatLength);
            var observacao = LogMessageSanitizer.SanitizeOptional(request.Observacao, MaxObservacaoLength);

            // ✅ Idempotente por natureza (merge lógico na leitura, ver AiMetricsReaderService) —
            // não valida se o layout já existe no histórico; se não existir, o merge simplesmente
            // não casa com nada, sem erro (ver dotnet-standards.md — resiliência).
            try
            {
                using (LogContext.PushProperty("Source", "AiMetrics"))
                {
                    _logger.LogInformation(
                        "Cypress validado. Layout={Layout} CypressValidado={CypressValidado} CStatPollux={CStatPollux} Observacao={Observacao}",
                        layout, request.CypressValidado, cStatPollux, observacao);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gravar resultado do Cypress para o layout {Layout}", layout);
                return StatusCode(500, new { success = false, error = "Erro ao gravar resultado do Cypress." });
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// Ingestão de um LOTE de gerações produzidas pelo job ai/XslSynth --mode=metrics-batch, que
        /// roda numa VM Linux separada (172.25.32.31) e grava o log dele lá, não no diretório de log
        /// que esta API lê (Windows). Sem este endpoint, as linhas "Geracao concluida." nunca chegam
        /// ao <see cref="IAiMetricsReaderService"/>: GET /generations vem vazio e o merge do
        /// POST /cypress-result não encontra geração alguma pra casar (§A4 de
        /// docs/architecture/handoff-job2-cypress-batch.md). A VM passa a EMPURRAR o resultado por
        /// HTTP; a API segue passiva.
        /// Cada item vira uma linha Serilog "Geracao concluida." (Source=AiMetrics) no mesmo log já
        /// lido pelo painel — nenhuma fonte da verdade nova. Item inválido é ignorado sozinho, sem
        /// derrubar o lote (a resposta diz quantos e por quê).
        /// Reenviar o mesmo lote é seguro: a leitura colapsa duplicatas por (Layout, Timestamp).
        /// </summary>
        /// <param name="request">Array de gerações — as mesmas chaves da linha "Geracao concluida.".</param>
        /// <returns>Contagem de recebidos/ingeridos/ignorados (<see cref="AiMetricsIngestResult"/>).</returns>
        /// <remarks>
        /// Exige o header <c>X-AiMetrics-Key</c> (ver <see cref="AiMetricsIngestKeyFilter"/>): é
        /// escrita que vira número no painel da diretoria, e a app não tem pipeline de autenticação.
        /// </remarks>
        [HttpPost("generations/ingest")]
        [ServiceFilter(typeof(AiMetricsIngestKeyFilter))]
        public IActionResult PostGenerationsIngest([FromBody] List<AiMetricsGenerationIngestRequest>? request)
        {
            if (request is null || request.Count == 0)
                return BadRequest(new { success = false, error = "Corpo deve ser um array com ao menos uma geração." });

            if (request.Count > _aiMetricsIngestService.TamanhoMaximoLote)
                return BadRequest(new { success = false, error = $"Lote excede o máximo de {_aiMetricsIngestService.TamanhoMaximoLote} gerações por requisição." });

            try
            {
                var result = _aiMetricsIngestService.IngestGenerations(request);

                _logger.LogInformation(
                    "Ingestão de métricas de IA concluída. Recebidos={Recebidos} Ingeridos={Ingeridos} Ignorados={Ignorados}",
                    result.Recebidos, result.Ingeridos, result.Ignorados);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao ingerir lote de gerações de métricas de IA ({Total} item(ns))", request.Count);
                return StatusCode(500, new { success = false, error = "Erro ao ingerir gerações de métricas de IA." });
            }
        }
    }
}
