using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XslSynth.Core;

namespace XslSynth.Synthesis;

/// <summary>
/// Sintetizador que usa um LLM LOCAL via Ollama (/api/generate).
/// LLM local mantém dado fiscal sensível no servidor (regra de segurança do projeto):
/// nuvem só com dado anonimizado/sintético e autorização explícita.
///
/// Config por env:
///   OLLAMA_URL    (default http://localhost:11434)
///   OLLAMA_MODEL  (default qwen2.5-coder)
/// </summary>
public sealed class OllamaXslSynthesizer : IXslSynthesizer
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _model;
    private readonly Action<string> _log;

    public OllamaXslSynthesizer(Action<string>? log = null)
    {
        _url = (Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434").TrimEnd('/');
        _model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen2.5-coder";
        _log = log ?? Console.WriteLine;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public string Name => $"Ollama ({_model} @ {_url})";

    public async Task<IReadOnlyList<RuleFragment>> SynthesizeRulesAsync(
        SynthesisBriefing briefing, CancellationToken ct = default)
    {
        var fragments = new List<RuleFragment>();

        foreach (var rule in briefing.Mapper.Rules.OrderBy(r => r.Sequence))
        {
            if (string.IsNullOrWhiteSpace(rule.TargetPath) || string.IsNullOrWhiteSpace(rule.ContentValue))
                continue;

            var segs = Xslt.Segments(rule.TargetPath);
            var parentPath = "/" + string.Join('/', segs[..^1]);
            var leafName = segs[^1];

            var prompt = BuildRulePrompt(briefing, rule.Name ?? leafName, leafName, rule.ContentValue!);
            var raw = await GenerateAsync(prompt, ct);
            var leafXsl = ExtractXsl(raw);

            if (!string.IsNullOrWhiteSpace(leafXsl))
                fragments.Add(new RuleFragment(rule.Name ?? leafName, parentPath, leafXsl));
            else
                _log($"[ollama] regra '{rule.Name}' não retornou XSLT utilizável.");
        }

        return fragments;
    }

    public async Task<string> RepairFromDiffAsync(
        string currentXsl, IReadOnlyList<NodeDiff> diffs, SynthesisBriefing briefing, CancellationToken ct = default)
    {
        if (diffs.Count == 0)
            return currentXsl;

        var prompt = BuildRepairPrompt(currentXsl, diffs);
        var raw = await GenerateAsync(prompt, ct);
        var repaired = ExtractXsl(raw);
        return string.IsNullOrWhiteSpace(repaired) ? currentXsl : repaired;
    }

    // ---- Prompts -----------------------------------------------------------

    private static string BuildRulePrompt(SynthesisBriefing briefing, string ruleName, string leafName, string csharp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um especialista em XSLT 1.0. Traduza a regra C# abaixo para um");
        sb.AppendLine($"ÚNICO elemento-folha XSLT chamado <{leafName}> que produz o valor da regra.");
        sb.AppendLine("Use o namespace xsl (http://www.w3.org/1999/XSL/Transform).");
        sb.AppendLine("Responda APENAS com o fragmento XSLT, sem explicação, sem cercas de código.");
        sb.AppendLine();
        sb.AppendLine($"Raiz de saída: {briefing.TargetRootName}");
        sb.AppendLine($"Regra: {ruleName}");
        if (!string.IsNullOrWhiteSpace(briefing.SeedXsl))
        {
            sb.AppendLine("XSL de referência (semente):");
            sb.AppendLine(briefing.SeedXsl);
        }
        sb.AppendLine("Código C#:");
        sb.AppendLine(csharp);
        return sb.ToString();
    }

    private static string BuildRepairPrompt(string currentXsl, IReadOnlyList<NodeDiff> diffs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um especialista em XSLT 1.0. O XSLT abaixo produz uma saída que");
        sb.AppendLine("DIVERGE do gabarito nos pontos listados. Corrija o XSLT para eliminar as");
        sb.AppendLine("divergências. Responda APENAS com o XSLT completo corrigido, sem explicação.");
        sb.AppendLine();
        sb.AppendLine("Divergências (XPath — esperado vs obtido):");
        foreach (var d in diffs)
            sb.AppendLine("  - " + d);
        sb.AppendLine();
        sb.AppendLine("XSLT atual:");
        sb.AppendLine(currentXsl);
        return sb.ToString();
    }

    // ---- HTTP Ollama -------------------------------------------------------

    private async Task<string> GenerateAsync(string prompt, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = _model,
                prompt,
                stream = false,
                options = new { temperature = 0.0 } // determinismo máximo p/ código
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{_url}/api/generate", content, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            _log($"[ollama] falha ao chamar {_url}/api/generate: {ex.Message}");
            return "";
        }
    }

    /// <summary>Extrai o XSLT da resposta do LLM (remove cercas ```xml ... ```).</summary>
    private static string ExtractXsl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var fenced = Regex.Match(raw, "```(?:xml|xslt)?\\s*(.*?)```", RegexOptions.Singleline);
        var text = fenced.Success ? fenced.Groups[1].Value : raw;
        return text.Trim();
    }
}
