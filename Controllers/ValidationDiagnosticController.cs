using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Diagnóstico de erros de validação (XSD/parsing) via LLM local (Ollama) — Gap 2 do contrato
    /// docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md. Deliberadamente um controller
    /// dedicado (não o XmlAnalysisController): à época, aquele controller dependia do
    /// GeminiAIService — não registrado no DI — e QUALQUER request a ele quebrava. Essa dependência
    /// já foi removida de lá, e o próprio GeminiAIService saiu do repositório em 2026-08-10 com o
    /// decommission de Gemini/OpenAI; a separação permanece porque a rota já está em uso pelo front.
    /// Endpoint exposto na mesma rota base "api/xml-analysis", só que num controller C# separado.
    /// </summary>
    [ApiController]
    [Route("api/xml-analysis")]
    public class ValidationDiagnosticController : ControllerBase
    {
        private readonly OllamaValidationDiagnosticService _diagnosticService;
        private readonly ILogger<ValidationDiagnosticController> _logger;

        public ValidationDiagnosticController(
            OllamaValidationDiagnosticService diagnosticService,
            ILogger<ValidationDiagnosticController> logger)
        {
            _diagnosticService = diagnosticService;
            _logger = logger;
        }

        /// <summary>
        /// Diagnostica um erro de validação (XSD ou parsing) usando o modelo local via Ollama.
        /// Ver tabela de decisão de casos-limite no contrato (Gap 2): baixa confiança NUNCA vira
        /// erro HTTP — só indisponibilidade/timeout/infra genérica viram (503/504/500).
        /// </summary>
        /// <param name="request"><c>ErrorMessage</c> obrigatório; <c>DocumentType</c>/<c>FieldName</c>/<c>TransformedXml</c> opcionais (mais contexto → diagnóstico melhor).</param>
        /// <param name="cancellationToken">Cancelamento do request HTTP.</param>
        /// <response code="200">Diagnóstico gerado (mesmo com baixa confiança — nunca vira erro HTTP).</response>
        /// <response code="400"><c>ErrorMessage</c> ausente.</response>
        /// <response code="500">Falha interna não catalogada.</response>
        /// <response code="503">Ollama indisponível.</response>
        /// <response code="504">Timeout ao consultar o Ollama.</response>
        [HttpPost("diagnose-validation-error")]
        public async Task<IActionResult> DiagnoseValidationError([FromBody] ValidationDiagnosticRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ErrorMessage))
            {
                return BadRequest(new ValidationDiagnosticResponse
                {
                    Success = false,
                    Error = "ErrorMessage é obrigatório"
                });
            }

            try
            {
                _logger.LogInformation(
                    "Diagnosticando erro de validação via Ollama. DocumentType={DocumentType}, FieldName={FieldName}, TemContextoCompleto={TemContexto}",
                    request.DocumentType, request.FieldName,
                    !string.IsNullOrWhiteSpace(request.FieldName) && !string.IsNullOrWhiteSpace(request.TransformedXml));

                var result = await _diagnosticService.DiagnoseAsync(request, cancellationToken);

                if (result.Success)
                {
                    return Ok(new ValidationDiagnosticResponse
                    {
                        Success = true,
                        Diagnostic = result.Diagnostic
                    });
                }

                return result.FailureKind switch
                {
                    DiagnosticFailureKind.Unavailable => StatusCode(503, new ValidationDiagnosticResponse
                    {
                        Success = false,
                        Error = result.ErrorMessage
                    }),
                    DiagnosticFailureKind.Timeout => StatusCode(504, new ValidationDiagnosticResponse
                    {
                        Success = false,
                        Error = result.ErrorMessage
                    }),
                    _ => StatusCode(500, new ValidationDiagnosticResponse
                    {
                        Success = false,
                        Error = result.ErrorMessage ?? "Erro interno ao processar diagnóstico"
                    })
                };
            }
            catch (Exception ex)
            {
                // Rede de segurança: qualquer exceção não prevista pelo serviço não deve vazar
                // stacktrace nem derrubar o processo — nunca deveria chegar aqui (o serviço já
                // encapsula tudo em ValidationDiagnosticResult), mas graceful degradation exige isso.
                _logger.LogError(ex, "Erro não tratado ao diagnosticar erro de validação");
                return StatusCode(500, new ValidationDiagnosticResponse
                {
                    Success = false,
                    Error = "Erro interno ao processar diagnóstico"
                });
            }
        }
    }
}
