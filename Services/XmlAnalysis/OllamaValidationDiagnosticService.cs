using System.Text;
using System.Text.Json;

using LayoutParserApi.Services.Llm;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Diagnostica erros de validação (XSD/parsing) usando o LLM local (Ollama) — decisão de
    /// arquitetura: Gemini/OpenAI foram decomissionados (ver [[gemini-openai-decommission-decision]]
    /// na memória do @lp-architect), Ollama assume 100% do papel de LLM neste projeto. Mantém dado
    /// fiscal potencialmente sensível (XML transformado, mensagens de erro) no servidor.
    /// ✅ Issue #340 (F1): consome <see cref="ILlmProvider"/> via <see cref="LlmProviderResolver"/>
    /// em vez de <see cref="HttpClient"/>/<see cref="OllamaOptions"/> diretos — mesma chamada
    /// HTTP por trás, agora atrás da abstração plugável (ver ADR
    /// docs/architecture/adr-llm-provider-plugavel-2026-09-08.md). Dado aqui é sempre
    /// <see cref="DataSensitivity.RealFiscalDocument"/>, hardcoded (não configurável via appsettings).
    /// </summary>
    public class OllamaValidationDiagnosticService
    {
        private readonly LlmProviderResolver _providerResolver;
        private readonly ILogger<OllamaValidationDiagnosticService> _logger;
        private readonly OllamaOptions _options;

        public OllamaValidationDiagnosticService(
            LlmProviderResolver providerResolver,
            IOptions<OllamaOptions> options,
            ILogger<OllamaValidationDiagnosticService> logger)
        {
            _providerResolver = providerResolver;
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
            var jsonSchema = JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new
                {
                    summary = new { type = "string" },
                    suggestedFix = new { type = "string" },
                    confidence = new { type = "number" }
                },
                required = new[] { "summary", "confidence" }
            });

            var llmRequest = new LlmRequest(prompt, DataSensitivity.RealFiscalDocument, jsonSchema, Temperature: 0.0);
            var provider = _providerResolver.Resolve(DataSensitivity.RealFiscalDocument);

            LlmResponse response;
            try
            {
                response = await provider.GenerateAsync(llmRequest, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cobre tanto o nosso timeoutCts quanto qualquer outro cancelamento da chamada
                // (ex.: cliente HTTP desconectou, cancellationToken do próprio request ASP.NET).
                _logger.LogWarning("Diagnóstico via Ollama cancelado/excedeu o timeout de {Timeout}s (modelo {Model})", timeoutSeconds, _options.Model);
                return ValidationDiagnosticResult.Fail(DiagnosticFailureKind.Timeout, "Diagnóstico excedeu o tempo limite");
            }

            if (response.TimedOut)
            {
                _logger.LogWarning("Diagnóstico via Ollama cancelado/excedeu o timeout de {Timeout}s (modelo {Model})", timeoutSeconds, _options.Model);
                return ValidationDiagnosticResult.Fail(DiagnosticFailureKind.Timeout, "Diagnóstico excedeu o tempo limite");
            }

            if (!response.Success)
            {
                _logger.LogWarning("Falha ao diagnosticar erro via {Provider}: {Error}", provider.Name, response.ErrorMessage);
                return ValidationDiagnosticResult.Fail(DiagnosticFailureKind.Infrastructure, "Erro de infraestrutura ao chamar o provedor de IA");
            }

            try
            {
                var diagnostic = ParseModelResponse(response.Text);
                return ValidationDiagnosticResult.Ok(diagnostic);
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

    }
}
