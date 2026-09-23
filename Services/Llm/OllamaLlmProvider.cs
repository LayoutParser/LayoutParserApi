using System.Text;
using System.Text.Json;

using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Llm
{
    /// <summary>
    /// Implementação de <see cref="ILlmProvider"/> por trás do Ollama (LLM local) — encapsula o que
    /// <see cref="XmlAnalysis.OllamaValidationDiagnosticService"/>/<see cref="Fiscal.MappingSuggestionService"/>
    /// já faziam via <see cref="HttpClient"/>/<see cref="OllamaOptions"/> crus, sem mudar comportamento
    /// observável (ADR docs/architecture/adr-llm-provider-plugavel-2026-09-08.md §3.4). Único provider
    /// registrado nesta fase (F1) — providers de nuvem ficam para F3/F4.
    /// </summary>
    public sealed class OllamaLlmProvider : ILlmProvider
    {
        private readonly HttpClient _httpClient;
        private readonly OllamaOptions _options;
        private readonly ILogger<OllamaLlmProvider> _logger;

        public string Name => "ollama-local";
        public ProviderLocality Locality => ProviderLocality.Local;

        public OllamaLlmProvider(HttpClient httpClient, IOptions<OllamaOptions> options, ILogger<OllamaLlmProvider> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            object? format = null;
            if (!string.IsNullOrWhiteSpace(request.JsonSchema))
            {
                try
                {
                    // ✅ JsonSchema é texto bruto (pode ser um schema estruturado — objeto — ou o
                    // literal "json" usado pelo modo genérico do Ollama) — reparseamos aqui pra
                    // embutir como valor real do campo "format" do payload, não como string escapada.
                    using var parsed = JsonDocument.Parse(request.JsonSchema);
                    format = parsed.RootElement.Clone();
                }
                catch (JsonException)
                {
                    _logger.LogDebug("JsonSchema informado em LlmRequest não é JSON válido; ignorando restrição de formato.");
                }
            }

            var payload = format is null
                ? new
                {
                    model = _options.Model,
                    prompt = request.Prompt,
                    stream = false,
                    options = new { temperature = request.Temperature }
                }
                : (object)new
                {
                    model = _options.Model,
                    prompt = request.Prompt,
                    stream = false,
                    format,
                    options = new { temperature = request.Temperature }
                };

            HttpResponseMessage response;
            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync($"{_options.Url.TrimEnd('/')}/api/generate", content, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Chamada ao Ollama cancelada/excedeu o timeout (modelo {Model})", _options.Model);
                return new LlmResponse(string.Empty, Success: false, TimedOut: true, ErrorMessage: "Tempo limite excedido");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Ollama indisponível em {Url}", _options.Url);
                return new LlmResponse(string.Empty, Success: false, ErrorMessage: "Provedor de IA indisponível no momento");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response);
                _logger.LogWarning("Ollama respondeu {StatusCode}: {Body}", response.StatusCode, body);
                return new LlmResponse(string.Empty, Success: false, ErrorMessage: $"Erro de infraestrutura ({(int)response.StatusCode})");
            }

            try
            {
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(raw);
                var text = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";
                return new LlmResponse(text, Success: true);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timeout ao ler resposta do Ollama (modelo {Model})", _options.Model);
                return new LlmResponse(string.Empty, Success: false, TimedOut: true, ErrorMessage: "Tempo limite excedido");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar resposta do Ollama");
                return new LlmResponse(string.Empty, Success: false, ErrorMessage: "Erro interno ao processar resposta do provedor de IA");
            }
        }

        public async Task<bool> IsReachableAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var response = await _httpClient.GetAsync($"{_options.Url.TrimEnd('/')}/api/tags", cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
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
