using System.Text.Json;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

using Serilog.Context;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Recurso "Logs" do ecossistema LayoutParser: ingestão de log do front-end (POST client)
    /// e leitura unificada/paginada dos 3 arquivos de log (GET) — Api, Lib e Decrypt.
    /// Decisão de posicionamento: não estendemos o MonitoringController porque ele é focado no
    /// domínio de negócio (análise/validação de layouts cadastrados), um contexto diferente de
    /// "operar sobre arquivos de log". Um controller dedicado mantém ingestão e leitura como um
    /// único recurso coeso ("logs"), sem poluir o Monitoring existente.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class LogsController : ControllerBase
    {
        // Níveis aceitos pelo endpoint de ingestão (Tarefa 1) — mapeiam 1:1 pros métodos do ILogger.
        private static readonly HashSet<string> AllowedLevels = new(StringComparer.OrdinalIgnoreCase)
        {
            "Debug", "Information", "Warning", "Error"
        };

        private const int MaxMessageLength = 4000;
        private const int MaxContextLength = 2000;

        private readonly ILogger<LogsController> _logger;
        private readonly IUnifiedLogReaderService _unifiedLogReaderService;

        public LogsController(ILogger<LogsController> logger, IUnifiedLogReaderService unifiedLogReaderService)
        {
            _logger = logger;
            _unifiedLogReaderService = unifiedLogReaderService;
        }

        /// <summary>
        /// Recebe log do front-end (LayoutParserReact) e grava no MESMO Serilog já configurado
        /// (sem sink/arquivo novo), marcado com Source=Frontend só no escopo desta requisição.
        /// </summary>
        [HttpPost("client")]
        public IActionResult PostClientLog([FromBody] ClientLogRequest? request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Message))
                return BadRequest(new { success = false, error = "Campo 'message' é obrigatório." });

            if (string.IsNullOrWhiteSpace(request.Level) || !AllowedLevels.Contains(request.Level))
                return BadRequest(new { success = false, error = "Campo 'level' inválido. Valores aceitos: Debug, Information, Warning, Error." });

            // ✅ Log de cliente é "melhor esforço": qualquer falha aqui é capturada e logada,
            // NUNCA propagada como erro pro front-end (o request sempre responde rápido).
            try
            {
                var sanitizedMessage = SanitizeMessage(request.Message);
                var contextText = SerializeContext(request.Context);
                var logMessage = contextText is null ? sanitizedMessage : $"{sanitizedMessage} | context: {contextText}";

                using (LogContext.PushProperty("Source", "Frontend"))
                {
                    switch (request.Level.ToLowerInvariant())
                    {
                        case "debug":
                            _logger.LogDebug("{ClientMessage}", logMessage);
                            break;
                        case "information":
                            _logger.LogInformation("{ClientMessage}", logMessage);
                            break;
                        case "warning":
                            _logger.LogWarning("{ClientMessage}", logMessage);
                            break;
                        case "error":
                            _logger.LogError("{ClientMessage}", logMessage);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao gravar log de cliente (ignorada, resposta segue OK)");
            }

            return NoContent();
        }

        /// <summary>
        /// Leitura unificada e paginada dos 3 arquivos de log do ecossistema (Api/Lib/Decrypt).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetLogs([FromQuery] UnifiedLogFilter filter)
        {
            try
            {
                var result = await _unifiedLogReaderService.GetLogsAsync(filter);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao consultar logs unificados");
                return StatusCode(500, new { success = false, error = "Erro ao consultar logs." });
            }
        }

        /// <summary>
        /// Neutraliza quebra de linha bruta (evita que uma única mensagem vire várias entradas
        /// falsas no parser de GET api/logs) e limita o tamanho.
        /// </summary>
        private static string SanitizeMessage(string message)
        {
            var flattened = message.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            return flattened.Length > MaxMessageLength ? flattened[..MaxMessageLength] : flattened;
        }

        private string? SerializeContext(object? context)
        {
            if (context is null)
                return null;

            try
            {
                var json = JsonSerializer.Serialize(context);
                var flattened = json.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                return flattened.Length > MaxContextLength ? flattened[..MaxContextLength] : flattened;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Não foi possível serializar o context do log de cliente, ignorado");
                return null;
            }
        }
    }
}
