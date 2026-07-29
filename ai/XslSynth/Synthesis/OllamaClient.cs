using System.Text;
using System.Text.Json;

namespace XslSynth.Synthesis;

/// <summary>
/// Cliente fino do Ollama (/api/generate) reutilizável pelos sintetizadores.
/// LLM LOCAL: mantém dado fiscal sensível no servidor (regra de segurança do projeto);
/// nuvem só com dado anonimizado/sintético e autorização explícita.
///
/// Config por env:
///   OLLAMA_URL    (default http://localhost:11434)
///   OLLAMA_MODEL  (default qwen2.5-coder:7b)
/// </summary>
public sealed class OllamaClient
{
    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public string Url { get; }
    public string Model { get; }

    public OllamaClient(Action<string>? log = null)
    {
        Url = (Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434").TrimEnd('/');
        Model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen2.5-coder:7b";
        _log = log ?? Console.WriteLine;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
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
            _log($"[ollama] falha ao chamar {Url}/api/generate: {ex.Message}");
            return ("", new OllamaGenerationMetrics(promptChars: prompt.Length, TokensPerSecond: 0,
                DurationSeconds: sw.Elapsed.TotalSeconds, Success: false));
        }
    }
}

/// <summary>Métricas de UMA chamada de geração — insumo direto do log estruturado AiMetrics.</summary>
public sealed record OllamaGenerationMetrics(
    int promptChars, double TokensPerSecond, double DurationSeconds, bool Success)
{
    public int PromptChars => promptChars;
}
