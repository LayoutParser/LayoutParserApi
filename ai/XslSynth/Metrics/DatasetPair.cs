using System.Text.Json;
using System.Text.Json.Serialization;

namespace XslSynth.Metrics;

/// <summary>
/// Um par held-out do dataset de fine-tuning (Fase 2), 1 linha do
/// <c>dataset_pairs_filtered_v2.jsonl</c> (54 pares reais: NFe=33/CTe=15/MDFe=6).
/// Espelha exatamente as chaves produzidas por <c>build_dataset.py</c>/<c>filter_dataset_v2.py</c>
/// — schema TCL (entrada posicional) → XSLT completo (saída), não regras DSL isoladas
/// (diferente do <see cref="XslSynth.Synthesis.FewShotIndex"/>, que indexa <c>MapperRule</c>).
/// </summary>
public sealed class DatasetPair
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("doc_type")] public string DocType { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("source_system")] public string? SourceSystem { get; init; }
    [JsonPropertyName("target_system")] public string? TargetSystem { get; init; }
    [JsonPropertyName("input_map_tcl_path")] public string? InputMapTclPath { get; init; }
    [JsonPropertyName("input_map_tcl")] public string InputMapTcl { get; init; } = "";
    [JsonPropertyName("output_xslt_path")] public string? OutputXsltPath { get; init; }
    [JsonPropertyName("output_xslt")] public string OutputXslt { get; init; } = "";

    /// <summary>Carrega o dataset inteiro (uma linha JSON por par) — degrade: linha
    /// ilegível é pulada e logada, não derruba o carregamento inteiro.</summary>
    public static List<DatasetPair> Load(string jsonlPath, Action<string>? log = null)
    {
        var pares = new List<DatasetPair>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var linha = 0;
        foreach (var raw in File.ReadLines(jsonlPath))
        {
            linha++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var par = JsonSerializer.Deserialize<DatasetPair>(raw, opts);
                if (par is not null) pares.Add(par);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[dataset] linha {linha} ilegível ({ex.Message}) — pulada.");
            }
        }
        return pares;
    }
}
