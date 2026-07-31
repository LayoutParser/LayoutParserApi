using System.Text;
using System.Text.Json;

namespace XslSynth.Synthesis;

/// <summary>
/// Cliente fino do Ollama (/api/generate) reutilizável pelos sintetizadores.
/// LLM LOCAL: mantém dado fiscal sensível no servidor (regra de segurança do projeto);
/// nuvem só com dado anonimizado/sintético e autorização explícita.
///
/// Config por env (todas sobrescritíveis pelo construtor):
///   OLLAMA_URL              (default http://localhost:11434)
///   OLLAMA_MODEL            (default qwen2.5-coder:7b)
///   OLLAMA_TIMEOUT_MINUTES  (default 15)
/// </summary>
public sealed class OllamaClient
{
    /// <summary>
    /// Teto por chamada. Era 5 min — a 22 s do caso mais lento já MEDIDO na VM (277,8 s a
    /// 3,685 tok/s), e os casos medidos eram CT-e, com TCL menor que o grosso do dataset
    /// (NFe = 33 dos 54 pares). Estourar o timeout cai no catch genérico e o caso é gravado
    /// como falha com 0 tok/s — indistinguível de "modelo não respondeu" —, o que enviesaria
    /// exatamente a métrica que decide qual modelo cabe no servidor sem GPU.
    /// </summary>
    private const int TimeoutPadraoMinutos = 15;

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public string Url { get; }
    public string Model { get; }
    public TimeSpan Timeout => _http.Timeout;

    /// <param name="model">Modelo a usar. Null ⇒ <c>OLLAMA_MODEL</c> ⇒ default.
    /// Passar aqui é o ÚNICO jeito de o modelo escolhido chegar ao Ollama — a propriedade é
    /// somente-leitura e é ela que vai no payload de <c>/api/generate</c>.</param>
    /// <param name="url">Endereço do servidor. Null ⇒ <c>OLLAMA_URL</c> ⇒ default.</param>
    /// <param name="timeout">Teto por chamada. Null ⇒ <c>OLLAMA_TIMEOUT_MINUTES</c> ⇒ 15 min.</param>
    public OllamaClient(Action<string>? log = null, string? model = null, string? url = null, TimeSpan? timeout = null)
    {
        Url = (url ?? Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434").TrimEnd('/');
        Model = model ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen2.5-coder:7b";
        _log = log ?? Console.WriteLine;

        var minutos = timeout?.TotalMinutes
            ?? (double.TryParse(Environment.GetEnvironmentVariable("OLLAMA_TIMEOUT_MINUTES"), out var m) && m > 0
                ? m
                : TimeoutPadraoMinutos);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(minutos) };
    }

    /// <summary>Verifica se o servidor Ollama responde (GET /api/tags).</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{Url}/api/tags", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log($"[ollama] indisponível em {Url}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Gera texto (stream=false, temperature=0 p/ determinismo). "" em falha.</summary>
    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var (response, _) = await GenerateWithMetricsAsync(prompt, ct);
        return response;
    }

    /// <summary>
    /// Gera texto e devolve também as métricas nativas do Ollama (tokens/s, tamanho do
    /// prompt, duração) — usado pelo job de métricas em lote (metrics-batch). Em falha
    /// (Ollama fora do ar, timeout, etc.), devolve resposta "" e métricas zeradas — o
    /// chamador decide como registrar o caso (nunca lança: resiliência do job em lote).
    /// </summary>
    public async Task<(string Response, OllamaGenerationMetrics Metrics)> GenerateWithMetricsAsync(
        string prompt, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = Model,
                prompt,
                stream = false,
                options = new { temperature = 0.0 }
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{Url}/api/generate", content, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            using var doc = JsonDocument.Parse(body);
            var texto = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";

            long EvalCount() => doc.RootElement.TryGetProperty("eval_count", out var e) ? e.GetInt64() : 0;
            long EvalDurationNs() => doc.RootElement.TryGetProperty("eval_duration", out var e) ? e.GetInt64() : 0;
            long TotalDurationNs() => doc.RootElement.TryGetProperty("total_duration", out var e) ? e.GetInt64() : 0;

            var evalCount = EvalCount();
            var evalDurationNs = EvalDurationNs();
            var totalDurationNs = TotalDurationNs();
            var tokensPerSecond = evalDurationNs > 0 ? evalCount / (evalDurationNs / 1_000_000_000.0) : 0.0;
            var durationSeconds = totalDurationNs > 0 ? totalDurationNs / 1_000_000_000.0 : sw.Elapsed.TotalSeconds;

            return (texto, new OllamaGenerationMetrics(promptChars: prompt.Length, tokensPerSecond, durationSeconds,
                Success: true));
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Timeout do HttpClient chega como TaskCanceledException SEM cancelamento pedido
            // pelo chamador. Marcá-lo é o que separa "o modelo é lento demais para este teto"
            // de "o modelo não respondeu" — duas conclusões opostas na hora de escolher modelo.
            var estourouTempo = ex is TaskCanceledException or TimeoutException && !ct.IsCancellationRequested;
            _log(estourouTempo
                ? $"[ollama] TIMEOUT após {_http.Timeout.TotalMinutes:F0} min em {Url}/api/generate "
                  + "(não é falha do modelo: aumente OLLAMA_TIMEOUT_MINUTES)"
                : $"[ollama] falha ao chamar {Url}/api/generate: {ex.Message}");
            return ("", new OllamaGenerationMetrics(promptChars: prompt.Length, TokensPerSecond: 0,
                DurationSeconds: sw.Elapsed.TotalSeconds, Success: false, TimedOut: estourouTempo));
        }
    }
}

/// <summary>Métricas de UMA chamada de geração — insumo direto do log estruturado AiMetrics.</summary>
/// <param name="TimedOut">Estourou o teto de tempo (≠ modelo sem resposta). Ver o catch de
/// <see cref="OllamaClient.GenerateWithMetricsAsync"/>.</param>
public sealed record OllamaGenerationMetrics(
    int promptChars, double TokensPerSecond, double DurationSeconds, bool Success, bool TimedOut = false)
{
    public int PromptChars => promptChars;
}
