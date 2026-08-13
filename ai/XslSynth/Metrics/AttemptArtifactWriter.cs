using System.Text.Json;
using System.Text.Json.Serialization;

namespace XslSynth.Metrics;

/// <summary>
/// Persistência de AUDITORIA do Job 1 (issue #35 / gap #2 de
/// docs/../.claude/agent-memory/lp-architect/ai-metrics-job1-job2-gaps.md): hoje o job gera
/// o XSLT, valida em memória (<see cref="OutputValidator"/>) e descarta tudo — nada sobrevive
/// além da linha de log agregada. Este arquivo grava TODO candidato gerado (o XSLT bruto, o
/// prompt que o produziu e o resultado da validação), independente de o caso virar (ou não)
/// um XML elegível ao Pollux.
///
/// É um artefato SEPARADO do contrato Job1→Job2 (<see cref="RunManifest"/>/<c>manifest.json</c>,
/// shape FIXO e lido pelo Cypress) — este <c>attempts-manifest.json</c> é só para
/// diagnóstico/auditoria humana (ex.: "por que este caso não ficou elegível?", "o que o
/// modelo respondeu de fato?") e não é consumido por nenhum job downstream. Por não ter
/// contrato externo, o shape aqui pode evoluir livremente.
/// </summary>
public sealed class AttemptManifest
{
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("startedAt")] public string StartedAt { get; init; } = "";
    [JsonPropertyName("finishedAt")] public string FinishedAt { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("totalAttempts")] public int TotalAttempts { get; init; }
    [JsonPropertyName("attempts")] public IReadOnlyList<AttemptRecord> Attempts { get; init; } = [];
}

/// <summary>Uma tentativa de geração (um caso do dataset, um candidato do LLM).</summary>
public sealed class AttemptRecord
{
    /// <summary>Mesmo id do <see cref="DatasetPair.Id"/> — igual ao campo <c>layout</c> do
    /// <see cref="ManifestCandidate"/>, para permitir cruzar os dois manifestos pelo mesmo caso.</summary>
    [JsonPropertyName("layoutName")] public string LayoutName { get; init; } = "";
    [JsonPropertyName("timestamp")] public string Timestamp { get; init; } = "";
    [JsonPropertyName("modeloOllama")] public string ModeloOllama { get; init; } = "";
    [JsonPropertyName("sucesso")] public bool Sucesso { get; init; }
    [JsonPropertyName("erro")] public string? Erro { get; init; }

    /// <summary>Caminho RELATIVO ao diretório do run (<c>attempts/&lt;id&gt;.xsl</c>) — o XSLT
    /// GERADO PELO LLM (não a saída da transformação, que é o que <see cref="ManifestCandidate"/>
    /// guarda). Null quando o Ollama não devolveu nada aproveitável.</summary>
    [JsonPropertyName("xsltGerado")] public string? XsltGeradoPath { get; init; }

    /// <summary>Caminho RELATIVO do prompt exato enviado ao Ollama para este caso (inclui os
    /// exemplos few-shot recuperados). Guardado em arquivo à parte — não inline — porque o
    /// prompt real inclui o XSLT de 2-3 exemplos análogos e facilmente passa de dezenas de KB.</summary>
    [JsonPropertyName("promptUsado")] public string? PromptUsadoPath { get; init; }

    [JsonPropertyName("validationResult")] public AttemptValidation? ValidationResult { get; init; }

    [JsonPropertyName("tokensPorSegundo")] public double TokensPorSegundo { get; init; }
    [JsonPropertyName("duracaoSegundos")] public double DuracaoSegundos { get; init; }
}

/// <summary>Cópia serializável do resultado hoje calculado em memória por
/// <see cref="OutputValidator"/> — antes descartado assim que o log agregado era escrito.</summary>
public sealed class AttemptValidation
{
    [JsonPropertyName("wellFormedXml")] public bool WellFormedXml { get; init; }
    [JsonPropertyName("tagOverlapRatio")] public double TagOverlapRatio { get; init; }
    [JsonPropertyName("textSimilarityRatio")] public double TextSimilarityRatio { get; init; }
    [JsonPropertyName("xsdValid")] public bool? XsdValid { get; init; }
    [JsonPropertyName("fewShotSimilarity")] public double FewShotSimilarity { get; init; }
    [JsonPropertyName("parseError")] public string? ParseError { get; init; }
}

/// <summary>
/// Grava <c>attempts/&lt;id&gt;.xsl</c> + <c>attempts/&lt;id&gt;.prompt.txt</c> por caso, e o
/// <c>attempts-manifest.json</c> agregado ao final (mesmo padrão de commit do
/// <see cref="RunArtifactWriter"/>: escreve num <c>.tmp</c> e troca por <c>rename</c>).
/// Resiliência: qualquer falha de escrita aqui é logada e NÃO derruba o lote — a auditoria é
/// valiosa, mas não pode ser motivo de o job de métricas parar de rodar.
/// </summary>
public sealed class AttemptArtifactWriter
{
    // WriteIndented: assim como o manifest.json do Job 2, este arquivo é lido por humanos.
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly Action<string> _log;
    private readonly List<AttemptRecord> _attempts = [];

    public string RunDirectory { get; }
    public string AttemptsDirectory { get; }

    public AttemptArtifactWriter(string runDirectory, Action<string>? log = null)
    {
        RunDirectory = Path.GetFullPath(runDirectory);
        AttemptsDirectory = Path.Combine(RunDirectory, "attempts");
        _log = log ?? Console.WriteLine;
        Directory.CreateDirectory(AttemptsDirectory);
    }

    /// <summary>Registra uma tentativa (grava os arquivos de XSLT/prompt na hora; o registro só
    /// entra no manifesto agregado em <see cref="TryCommit"/>, ao final do lote).</summary>
    public void RecordAttempt(string layoutId, string modelo, bool sucesso, string? erro,
        string? xsltGerado, string? promptUsado, bool wellFormedXml, double tagOverlapRatio,
        double textSimilarityRatio, bool? xsdValid, double fewShotSimilarity, string? parseError,
        double tokensPorSegundo, double duracaoSegundos)
    {
        var safeId = ToSafeFileName(layoutId);
        string? xsltPath = TryWriteArtifact(safeId + ".xsl", xsltGerado, layoutId, "XSLT gerado");
        string? promptPath = TryWriteArtifact(safeId + ".prompt.txt", promptUsado, layoutId, "prompt");

        _attempts.Add(new AttemptRecord
        {
            LayoutName = layoutId,
            Timestamp = RunArtifactWriter.Iso(DateTime.UtcNow),
            ModeloOllama = modelo,
            Sucesso = sucesso,
            Erro = erro,
            XsltGeradoPath = xsltPath,
            PromptUsadoPath = promptPath,
            ValidationResult = new AttemptValidation
            {
                WellFormedXml = wellFormedXml,
                TagOverlapRatio = tagOverlapRatio,
                TextSimilarityRatio = textSimilarityRatio,
                XsdValid = xsdValid,
                FewShotSimilarity = fewShotSimilarity,
                ParseError = parseError
            },
            TokensPorSegundo = tokensPorSegundo,
            DuracaoSegundos = duracaoSegundos
        });
    }

    private string? TryWriteArtifact(string fileName, string? conteudo, string layoutId, string descricao)
    {
        if (string.IsNullOrEmpty(conteudo)) return null;
        try
        {
            var destino = Path.Combine(AttemptsDirectory, fileName);
            File.WriteAllText(destino, conteudo, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return $"attempts/{fileName}";
        }
        catch (Exception ex)
        {
            _log($"   [attempts] falha ao gravar {descricao} de {layoutId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Publica o <c>attempts-manifest.json</c> agregado. Best-effort: diferente do
    /// commit do Job 2, a ausência deste arquivo não invalida o run — é só auditoria a menos.</summary>
    public bool TryCommit(string runId, DateTime startedAt, DateTime finishedAt, string modelo)
    {
        var manifesto = new AttemptManifest
        {
            RunId = runId,
            StartedAt = RunArtifactWriter.Iso(startedAt),
            FinishedAt = RunArtifactWriter.Iso(finishedAt),
            Model = modelo,
            TotalAttempts = _attempts.Count,
            Attempts = _attempts
        };

        var destino = Path.Combine(RunDirectory, "attempts-manifest.json");
        var temporario = destino + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(manifesto, JsonOpts);
            File.WriteAllText(temporario, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporario, destino, overwrite: true);
            _log($"   [attempts] manifesto de auditoria publicado: {destino} ({_attempts.Count} tentativa(s)).");
            return true;
        }
        catch (Exception ex)
        {
            _log($"   [attempts] falha ao publicar manifesto de auditoria: {ex.Message}");
            TryDelete(temporario);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ }
    }

    private static string ToSafeFileName(string id)
    {
        var invalidos = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => invalidos.Contains(c) || c is '\\' or '/' ? '_' : c).ToArray();
        return new string(chars);
    }
}
