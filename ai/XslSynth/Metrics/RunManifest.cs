using System.Text.Json;
using System.Text.Json.Serialization;

namespace XslSynth.Metrics;

/// <summary>
/// Manifesto de um run do <c>--mode=metrics-batch</c> — contrato de ENTRADA do Job 2
/// (suíte Cypress em modo batch). Especificação normativa:
/// docs/architecture/handoff-job2-cypress-batch.md §2.
///
/// O shape é FIXO: nenhum campo a mais, a menos ou renomeado — o Job 2 (repo
/// LayoutParserCypress) lê este arquivo em <c>setupNodeEvents</c> e gera um <c>it()</c>
/// por candidato. Mudança de shape aqui exige bump de <see cref="SchemaVersion"/> e
/// acordo com o dono do Job 2.
/// </summary>
public sealed class RunManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("startedAt")] public string StartedAt { get; init; } = "";
    [JsonPropertyName("finishedAt")] public string FinishedAt { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("totalCases")] public int TotalCases { get; init; }

    /// <summary>Pode ser vazio — é estado VÁLIDO (rodada sem candidato submissível),
    /// tratado pelo Job 2 como skip limpo, nunca como erro (§2, regra 5).</summary>
    [JsonPropertyName("candidates")] public IReadOnlyList<ManifestCandidate> Candidates { get; init; } = [];
}

/// <summary>Um candidato do manifesto — 9 campos, exatamente os da §2 do handoff.</summary>
public sealed class ManifestCandidate
{
    /// <summary><see cref="Layout"/> com os separadores trocados por <c>_</c> (seguro
    /// para nome de arquivo). Ex.: <c>NFe_4.00_NFe009_4.00_EnvioNFe_NeoGridToSefaz</c>.</summary>
    [JsonPropertyName("candidateId")] public string CandidateId { get; init; } = "";

    /// <summary>
    /// CHAVE DE JUNÇÃO com a API — byte-a-byte igual ao campo <c>Layout</c> da linha Serilog
    /// <c>Geracao concluida.</c> (que é o <see cref="DatasetPair.Id"/>), COM barras invertidas.
    /// Normalizar para <c>/</c>, mudar a caixa ou cortar prefixo faz o merge do
    /// AiMetricsReaderService falhar em SILÊNCIO e o painel ficar vazio sem erro nenhum.
    /// Este valor é propagado sem nenhuma transformação — ver <see cref="RunArtifactWriter"/>.
    /// </summary>
    [JsonPropertyName("layout")] public string Layout { get; init; } = "";

    [JsonPropertyName("docType")] public string DocType { get; init; } = "";
    [JsonPropertyName("operation")] public string Operation { get; init; } = "";

    /// <summary>Regra decidida AQUI, no produtor (§2, regra 4): o Job 2 apenas filtra por
    /// esta flag e não reimplementa o critério. Ver <see cref="PolluxEligibility"/>.</summary>
    [JsonPropertyName("eligibleForPollux")] public bool EligibleForPollux { get; init; }

    /// <summary>Caminho RELATIVO ao diretório do run (ex.: <c>candidates/&lt;id&gt;.xml</c>).
    /// Só entram no manifesto candidatos cujo arquivo foi de fato gravado — o Job 2 nunca
    /// recebe um ponteiro para arquivo inexistente.</summary>
    [JsonPropertyName("xmlPath")] public string XmlPath { get; init; } = "";

    /// <summary>Nome do TXT de instância real que originou o XML. <c>null</c> quando o XML
    /// não veio de instância (nunca é preenchido com dado sintético — ver §A3 do handoff).</summary>
    [JsonPropertyName("sourceFixture")] public string? SourceFixture { get; init; }

    /// <summary><c>null</c> quando não há XSD aplicável a este caso (a maior parte do
    /// dataset: cancelamento/consulta/CT-e/MDF-e não têm oráculo plugado aqui).</summary>
    [JsonPropertyName("xsdValid")] public bool? XsdValid { get; init; }

    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

/// <summary>
/// Escrita dos artefatos do run com COMMIT ATÔMICO (§2, regra 3):
/// XMLs primeiro, <c>manifest.json</c> por último via arquivo temporário + <c>rename</c>.
///
/// A presença do manifesto é o sinal "run completo" para o Job 2. Job 1 morto no meio ⇒
/// existem XMLs órfãos em <c>candidates/</c> mas NÃO existe manifesto ⇒ o Job 2 não roda e
/// nenhum dado pela metade entra no painel. O inverso (manifesto apontando para XML que não
/// existe) é o modo de falha que este desenho impede.
/// </summary>
public sealed class RunArtifactWriter
{
    // WriteIndented: o manifesto é lido por humanos no diagnóstico da VM.
    // NÃO usar DefaultIgnoreCondition.WhenWritingNull: o contrato mostra "xsdValid": null
    // e "notes": null PRESENTES no JSON — omiti-los mudaria o shape acordado.
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly Action<string> _log;

    public string RunDirectory { get; }
    public string RunId { get; }
    public string CandidatesDirectory { get; }

    public RunArtifactWriter(string runDirectory, string runId, Action<string>? log = null)
    {
        RunDirectory = Path.GetFullPath(runDirectory);
        RunId = runId;
        CandidatesDirectory = Path.Combine(RunDirectory, "candidates");
        _log = log ?? Console.WriteLine;
        Directory.CreateDirectory(CandidatesDirectory);
    }

    /// <summary>Grava <c>candidates/&lt;candidateId&gt;.xml</c> e devolve o caminho RELATIVO
    /// (com <c>/</c>, que é o que o Node do Job 2 espera) — ou null se a gravação falhar.</summary>
    public string? TryWriteCandidateXml(string candidateId, string xml)
    {
        try
        {
            var destino = Path.Combine(CandidatesDirectory, candidateId + ".xml");
            // Grava e força o flush em disco: o manifesto só é publicado depois, mas o
            // XML precisa estar durável antes do rename (não só no cache do processo).
            using (var fs = new FileStream(destino, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                w.Write(xml);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            return $"candidates/{candidateId}.xml";
        }
        catch (Exception ex)
        {
            _log($"   [run-dir] falha ao gravar XML de {candidateId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// COMMIT do run: serializa o manifesto num <c>.tmp</c> e faz <c>rename</c> por cima do
    /// destino final (atômico no mesmo filesystem). Depois — e só depois — atualiza o ponteiro
    /// <c>runs/latest</c>, que é conveniência de operação e não faz parte do commit.
    /// </summary>
    public bool TryCommit(RunManifest manifest)
    {
        var destino = Path.Combine(RunDirectory, "manifest.json");
        var temporario = destino + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(manifest, JsonOpts);
            using (var fs = new FileStream(temporario, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                w.Write(json);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.Move(temporario, destino, overwrite: true);   // rename(2) — atômico
            _log($"   [run-dir] manifesto publicado: {destino} ({manifest.Candidates.Count} candidato(s)).");
        }
        catch (Exception ex)
        {
            _log($"   [run-dir] ❌ falha ao publicar o manifesto: {ex.Message}");
            TryDelete(temporario);
            return false;
        }

        TryUpdateLatestPointer();
        return true;
    }

    /// <summary><c>$METRICS_HOME/runs/latest</c> — 1 linha com o runId mais recente (§2).
    /// Best-effort: falhar aqui NÃO invalida o run já commitado.</summary>
    private void TryUpdateLatestPointer()
    {
        try
        {
            var runsDir = Directory.GetParent(RunDirectory)?.FullName;
            if (runsDir is null) return;
            var ponteiro = Path.Combine(runsDir, "latest");
            var temporario = ponteiro + ".tmp";
            File.WriteAllText(temporario, RunId + Environment.NewLine);
            File.Move(temporario, ponteiro, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"   [run-dir] aviso: ponteiro 'latest' não atualizado ({ex.Message}) — run continua válido.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ }
    }

    /// <summary>runId no formato do contrato: timestamp UTC compacto (<c>20260801T000000Z</c>).</summary>
    public static string NewRunId(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'");

    /// <summary>Timestamp ISO-8601 UTC do manifesto (<c>2026-08-01T00:00:03Z</c>).</summary>
    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
