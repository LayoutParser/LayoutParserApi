using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Diagnostica erros de validação (XSD/parsing) usando o LLM local (Ollama) — decisão de
    /// arquitetura: Gemini/OpenAI foram decomissionados (ver [[gemini-openai-decommission-decision]]
    /// na memória do @lp-architect), Ollama assume 100% do papel de LLM neste projeto. Mantém dado
    /// fiscal potencialmente sensível (XML transformado, mensagens de erro) no servidor.
    /// </summary>
    public class OllamaValidationDiagnosticService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OllamaValidationDiagnosticService> _logger;
        private readonly OllamaOptions _options;

        public OllamaValidationDiagnosticService(
            HttpClient httpClient,
            IOptions<OllamaOptions> options,
            ILogger<OllamaValidationDiagnosticService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _options = options.Value;
        }

        public async Task<ValidationDiagnosticResult> DiagnoseAsync(ValidationDiagnosticRequest request, CancellationToken cancellationToken = default)
        {
            var prompt = BuildPrompt(request);

            // ✅ Timeout dedicado (Ollama:DiagnosisTimeoutSeconds) — não usa o Timeout padrão do
            // HttpClient (que pode ser maior/indefinido conforme registro no DI); cobre só esta chamada.
            var timeoutSeconds = _options.DiagnosisTimeoutSeconds > 0 ? _options.DiagnosisTimeoutSeconds : 60;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var payload = new
            {
                model = _options.Model,
                prompt,
                stream = false,
                // ✅ Saída estruturada nativa do Ollama (suportado a partir da v0.5, confirmado em
                // uso nesta instância v0.31.2 via teste manual): força o modelo a devolver
                // {summary, suggestedFix, confidence} como JSON válido em vez de depender de
                // parsing de texto livre por regex/heurística. Fallback de parsing de texto livre
                // ainda existe abaixo (ParseModelResponse) para o caso de o modelo não respeitar
                // o schema (acontece ocasionalmente com modelos menores sob temperature > 0).
                // Nota: usamos apenas "string"/"number" simples (sem union type) no schema — o
                // suporte de tipo nullable ("string"|"null") do JSON Schema completo não foi
                // validado contra esta versão do Ollama; pedimos "" via prompt quando não houver
                // sugestão e tratamos string vazia como null no parse (ParseModelResponse).
                format = new
                {
                    type = "object",
                    properties = new
                    {
                        summary = new { type = "string" },
                        suggestedFix = new { type = "string" },
                        confidence = new { type = "number" }
                    },
                    required = new[] { "summary", "confidence" }
                },
                options = new { temperature = 0.0 }
            };

            HttpResponseMessage response;
            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync($"{_options.Url.TrimEnd('/')}/api/generate", content, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cobre tanto o nosso timeoutCts quanto qualquer outro cancelamento da chamada
                // (ex.: cliente HTTP desconectou, cancellationToken do próprio request ASP.NET).
                // Tratar como Timeout é o fallback mais seguro aqui — nenhum dos dois casos é
                // "resposta obtida com sucesso", e nenhum é a mesma coisa que "Ollama indisponível"
                // (que exige HttpRequestException por connection refused).
                _logger.LogWarning("Diagnóstico via Ollama cancelado/excedeu o timeout de {Timeout}s (modelo {Model})", timeoutSeconds, _options.Model);
                return ValidationDiagnosticResult.Fail(DiagnosticFailureKind.Timeout, "Diagnóstico excedeu o tempo limite");
            }
            catch (HttpRequestException ex)
            {
                // Connection refused / DNS / socket — Ollama fora do ar ou inacessível.
                _logger.LogWarning(ex, "Ollama indisponível em {Url}", _options.Url);
                return ValidationDiagnosticResult.Fail(DiagnosticFailureKind.Unavailable, "Provedor de IA indisponível no momento");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response);
                _logger.LogWarning("Ollama respondeu {StatusCode} ao diagnosticar erro: {Body}", response.StatusCode, body);

                // Se o próprio Ollama devolveu erro de conexão indireto (ex.: 502 de um proxy na frente
                // dele), tratamos como indisponibilidade; qualquer outro código HTTP inesperado do
                // servidor Ollama em si é infraestrutura genérica.
                return ValidationDiagnosticResult.Fail(DiagnosticFailureKind.Infrastructure, "Erro de infraestrutura ao chamar o provedor de IA");
            }

            try
            {
                var raw = await response.Content.ReadAsStringAsync(linkedCts.Token);
                using var doc = JsonDocument.Parse(raw);
                var modelResponseText = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";

                var diagnostic = ParseModelResponse(modelResponseText);
                return ValidationDiagnosticResult.Ok(diagnostic);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("Timeout ao ler resposta do Ollama (modelo {Model})", _options.Model);
                return ValidationDiagnosticResult.Fail(DiagnosticFailureKind.Timeout, "Diagnóstico excedeu o tempo limite");
            }
            catch (Exception ex)
            {
                // Não vazar stacktrace ao cliente — logar completo aqui, devolver mensagem genérica.
                _logger.LogError(ex, "Erro de infraestrutura ao processar diagnóstico via Ollama");
                return ValidationDiagnosticResult.Fail(DiagnosticFailureKind.Infrastructure, "Erro interno ao processar diagnóstico");
            }
        }

        /// <summary>
        /// Tenta interpretar a saída do modelo como o schema estruturado pedido. Se o modelo não
        /// respeitou o schema (ex.: envolveu em texto explicativo, ou o parser JSON estrito falhou),
        /// cai para um resumo com o texto bruto e confiança conservadora — nunca lança exceção daqui,
        /// pois "não consegui estruturar" não é motivo pra virar erro HTTP (ver tabela de decisão).
        /// </summary>
        private ValidationDiagnostic ParseModelResponse(string modelResponseText)
        {
            if (string.IsNullOrWhiteSpace(modelResponseText))
            {
                return new ValidationDiagnostic
                {
                    Summary = "Não foi possível determinar com certeza a causa do erro — o modelo não retornou conteúdo.",
                    SuggestedFix = null,
                    Confidence = 0.1
                };
            }

            try
            {
                using var doc = JsonDocument.Parse(modelResponseText);
                var root = doc.RootElement;

                var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                string? suggestedFix = null;
                if (root.TryGetProperty("suggestedFix", out var sf) && sf.ValueKind == JsonValueKind.String)
                {
                    var sfValue = sf.GetString();
                    suggestedFix = string.IsNullOrWhiteSpace(sfValue) ? null : sfValue;
                }

                double? confidence = null;
                if (root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                    confidence = c.GetDouble();

                // ✅ Normalização defensiva: constatado em teste manual que o modelo local
                // (qwen2.5-coder:7b, mesmo padrão esperado no deepseek-coder:6.7b configurado)
                // ignora a instrução de escala 0.0–1.0 do prompt e devolve confidence em escala
                // 0–100 (ex.: 90 em vez de 0.9). Um Clamp ingênuo aqui truncaria pra 1.0 e
                // destruiria o sinal real do modelo. Se vier > 1, assume-se escala 0–100 e
                // normaliza antes de clampar; só então aplicamos o Clamp final como rede de
                // segurança para qualquer outro valor fora do intervalo.
                if (confidence.HasValue)
                {
                    if (confidence.Value > 1.0)
                        confidence = confidence.Value / 100.0;

                    confidence = Math.Clamp(confidence.Value, 0.0, 1.0);
                }

                if (string.IsNullOrWhiteSpace(summary))
                {
                    return new ValidationDiagnostic
                    {
                        Summary = "Não foi possível determinar com certeza a causa do erro.",
                        SuggestedFix = suggestedFix,
                        Confidence = confidence ?? 0.2
                    };
                }

                return new ValidationDiagnostic
                {
                    Summary = summary,
                    SuggestedFix = suggestedFix,
                    Confidence = confidence ?? 0.3
                };
            }
            catch (JsonException)
            {
                // Fallback: modelo não respeitou o schema — trata a resposta inteira como texto livre.
                // Confiança fixa e baixa porque não há sinal estruturado do próprio modelo aqui.
                _logger.LogDebug("Resposta do Ollama não veio em JSON estruturado; usando fallback de texto livre");
                return new ValidationDiagnostic
                {
                    Summary = modelResponseText.Trim(),
                    SuggestedFix = null,
                    Confidence = 0.3
                };
            }
        }

        private string BuildPrompt(ValidationDiagnosticRequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Você é um especialista em validação de documentos fiscais (NFe/CTe) e XML da SEFAZ,");
            sb.AppendLine("incluindo documentos originados de MQSeries/IDOC transformados para XML.");
            sb.AppendLine();
            sb.AppendLine("Diagnostique o seguinte erro de validação e responda SOMENTE com um objeto JSON");
            sb.AppendLine("com os campos: summary (string), suggestedFix (string; use \"\" se não tiver segurança");
            sb.AppendLine("suficiente pra sugerir uma correção), confidence (número 0.0 a 1.0).");
            sb.AppendLine();
            sb.AppendLine("REGRA DE CONFIANÇA: se as informações de contexto abaixo forem insuficientes");
            sb.AppendLine("(ex.: faltar o campo afetado, o XML transformado ou o segmento de origem),");
            sb.AppendLine("reduza o valor de confidence e deixe isso explícito no summary. Nunca invente");
            sb.AppendLine("contexto que não foi fornecido.");
            sb.AppendLine();
            sb.AppendLine("ERRO DE VALIDAÇÃO:");
            sb.AppendLine(request.ErrorMessage);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(request.DocumentType))
                sb.AppendLine($"TIPO DE DOCUMENTO: {request.DocumentType}");

            if (!string.IsNullOrWhiteSpace(request.FieldName))
                sb.AppendLine($"CAMPO AFETADO: {request.FieldName}");

            if (!string.IsNullOrWhiteSpace(request.MqSeriesSegment))
                sb.AppendLine($"SEGMENTO MQSERIES/IDOC DE ORIGEM: {request.MqSeriesSegment}");

            if (!string.IsNullOrWhiteSpace(request.TransformedXml))
            {
                sb.AppendLine("XML TRANSFORMADO (trecho, para contexto):");
                sb.AppendLine(request.TransformedXml.Substring(0, Math.Min(2000, request.TransformedXml.Length)));
            }

            sb.AppendLine();
            sb.AppendLine("Responda apenas com o JSON, sem markdown, sem explicações fora do JSON.");

            return sb.ToString();
        }

        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                return "";
            }
        }
    }
}
