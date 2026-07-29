using Serilog;
using Serilog.Context;
using XslSynth.Synthesis;

namespace XslSynth.Metrics;

/// <summary>Opções do modo <c>--mode=metrics-batch</c> (CLI args já resolvidos pelo Program.cs).</summary>
public sealed record MetricsBatchOptions(
    string DatasetPath, string Model, int FewShotK, int? Limit, string LogDirectory, string LogFileName);

/// <summary>Um resultado individual do lote (para o resumo agregado ao final).</summary>
internal sealed record CaseResult(
    string Layout, bool Sucesso, double TokensPerSecond, double DurationSeconds,
    double FewShotSimilarity, double TagOverlapRatio, double TextSimilarityRatio, string? Erro);

/// <summary>
/// Item 1 do plano de métricas de IA em produção (docs/architecture/plano-metricas-ia-servidor-producao.md):
/// eleva o spike pontual de RAG (1 caso, Python solto) a um modo de execução real dentro do
/// XslSynth, rodando em LOTE contra o dataset held-out completo (54 pares), com métricas
/// estruturadas via Serilog (Source=AiMetrics) — a série histórica real que faltava.
///
/// Loop por caso: recupera few-shot (held-out, nunca o próprio caso) → chama o Ollama local
/// → valida a saída (bem-formado + similaridade estrutural; XSD real fora de escopo, ver
/// <see cref="OutputValidator"/>) → loga → segue para o próximo MESMO se este caso falhar
/// (resiliência: um timeout/erro de Ollama não pode derrubar o lote inteiro rodando sozinho
/// no servidor por dias).
/// </summary>
public static class MetricsBatchRunner
{
    public static async Task<int> RunAsync(MetricsBatchOptions opts, Action<string> log, CancellationToken ct = default)
    {
        if (!File.Exists(opts.DatasetPath))
        {
            log($"❌ Dataset não encontrado: {opts.DatasetPath}");
            return 2;
        }

        // ── Serilog: mesmo padrão da API (Source via LogContext, arquivo compartilhado
        // quando possível, shared:true) — para o UnifiedLogReaderService já existente
        // enxergar a série sem precisar de dashboard novo (ver plano §4). ─────────────
        Directory.CreateDirectory(opts.LogDirectory);
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Src:{Source}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(opts.LogDirectory, opts.LogFileName),
                rollingInterval: Serilog.RollingInterval.Infinite,
                outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Src:{Source}] {Message:lj}{NewLine}{Exception}",
                shared: true)
            .CreateLogger();

        try
        {
            var pares = DatasetPair.Load(opts.DatasetPath, log);
            log($"[metrics-batch] dataset carregado: {pares.Count} pares "
                + $"({string.Join(", ", pares.GroupBy(p => p.DocType).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"))}).");

            if (pares.Count == 0)
            {
                log("❌ Dataset vazio ou 100% ilegível — nada a rodar.");
                return 1;
            }

            var index = DatasetFewShotIndex.Build(pares);
            var client = new OllamaClient(log) { }; // Model/Url via env; sobrescreve Model abaixo
            var modelo = opts.Model;

            if (!await client.IsReachableAsync(ct))
            {
                log($"❌ Ollama indisponível em {client.Url} — job de métricas não pode rodar (sem fallback: "
                    + "este modo EXISTE para medir o LLM real, um mock não produziria dado honesto).");
                return 1;
            }

            var casos = opts.Limit is { } lim && lim > 0 ? pares.Take(lim).ToList() : pares;
            log($"[metrics-batch] modelo={modelo} · few-shot k={opts.FewShotK} · casos nesta rodada={casos.Count}"
                + (opts.Limit is not null ? $" (LIMITADO de {pares.Count} — teste/validação, não rodada completa)" : ""));
            log("");

            var resultados = new List<CaseResult>();
            var n = 0;
            foreach (var caso in casos)
            {
                n++;
                ct.ThrowIfCancellationRequested();
                log($"[{n}/{casos.Count}] {caso.Id}");

                // ── Resiliência: QUALQUER exceção neste caso é capturada e logada como
                // falha DAQUELE caso — o lote inteiro continua para o próximo. ─────────
                try
                {
                    var resultado = await RunCaseAsync(caso, index, client, modelo, opts.FewShotK, log, ct);
                    resultados.Add(resultado);
                }
                catch (Exception ex)
                {
                    log($"   ❌ falha inesperada neste caso (job SEGUE para o próximo): {ex.Message}");
                    LogCaso(caso.Id, modelo, sucesso: false, tokensPorSegundo: 0, promptChars: 0,
                        duracaoSegundos: 0, fewShotSimilarity: 0, tagOverlap: 0, textSim: 0, xsdValido: null);
                    resultados.Add(new CaseResult(caso.Id, false, 0, 0, 0, 0, 0, ex.Message));
                }
            }

            LogResumo(resultados, modelo);
            return resultados.Any(r => r.Sucesso) ? 0 : 1;
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    private static async Task<CaseResult> RunCaseAsync(DatasetPair caso, DatasetFewShotIndex index,
        OllamaClient client, string modelo, int fewShotK, Action<string> log, CancellationToken ct)
    {
        var recuperados = index.Retrieve(caso, fewShotK);
        var similaridadeMedia = recuperados.Count > 0 ? recuperados.Average(m => m.Similarity) : 0.0;
        log($"   few-shot: {recuperados.Count} recuperado(s) "
            + (recuperados.Count > 0
                ? $"(top: {recuperados[0].Pair.Id}, sim={recuperados[0].Similarity:F3})"
                : "(nenhum análogo encontrado)"));

        var prompt = BuildPrompt(caso, recuperados);
        var (respostaBruta, metrics) = await client.GenerateWithMetricsAsync(prompt, ct);

        if (!metrics.Success || string.IsNullOrWhiteSpace(respostaBruta))
        {
            log("   ❌ Ollama não retornou saída utilizável para este caso.");
            LogCaso(caso.Id, modelo, sucesso: false, tokensPorSegundo: metrics.TokensPerSecond,
                promptChars: metrics.PromptChars, duracaoSegundos: metrics.DurationSeconds,
                fewShotSimilarity: similaridadeMedia, tagOverlap: 0, textSim: 0, xsdValido: null);
            return new CaseResult(caso.Id, false, metrics.TokensPerSecond, metrics.DurationSeconds,
                similaridadeMedia, 0, 0, "Ollama sem resposta utilizável");
        }

        var candidato = ExtractXml(respostaBruta);
        var validacao = OutputValidator.Validate(candidato, caso.OutputXslt);

        log($"   gerado em {metrics.DurationSeconds:F1}s ({metrics.TokensPerSecond:F2} tok/s) · "
            + $"bem-formado={(validacao.WellFormedXml ? "sim" : "não")} · "
            + $"tagOverlap={validacao.TagOverlapRatio:F3} · textSim={validacao.TextSimilarityRatio:F3}");

        LogCaso(caso.Id, modelo, sucesso: true, tokensPorSegundo: metrics.TokensPerSecond,
            promptChars: metrics.PromptChars, duracaoSegundos: metrics.DurationSeconds,
            fewShotSimilarity: similaridadeMedia, tagOverlap: validacao.TagOverlapRatio,
            textSim: validacao.TextSimilarityRatio, xsdValido: validacao.XsdValid);

        return new CaseResult(caso.Id, true, metrics.TokensPerSecond, metrics.DurationSeconds,
            similaridadeMedia, validacao.TagOverlapRatio, validacao.TextSimilarityRatio, null);
    }

    private static string BuildPrompt(DatasetPair caso, IReadOnlyList<DatasetFewShotMatch> recuperados)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Você é um especialista em XSLT 1.0 e nos leiautes fiscais brasileiros (NFe/CTe/MDFe).");
        sb.AppendLine("Gere o arquivo .xsl COMPLETO que transforma o schema TCL de entrada abaixo no XML de");
        sb.AppendLine($"saída no padrão SEFAZ ({caso.DocType} {caso.Version}). Responda APENAS com o XSLT,");
        sb.AppendLine("sem explicação, sem cercas de código.");
        sb.AppendLine();
        foreach (var (match, i) in recuperados.Select((m, i) => (m, i + 1)))
        {
            sb.AppendLine($"── Exemplo {i} (análogo, sim={match.Similarity:F2}) ──");
            sb.AppendLine("TCL:");
            sb.AppendLine(match.Pair.InputMapTcl);
            sb.AppendLine("XSLT esperado:");
            sb.AppendLine(match.Pair.OutputXslt);
            sb.AppendLine();
        }
        sb.AppendLine("── Caso a resolver ──");
        sb.AppendLine("TCL:");
        sb.AppendLine(caso.InputMapTcl);
        sb.AppendLine("XSLT:");
        return sb.ToString();
    }

    private static string ExtractXml(string raw)
    {
        var fenced = System.Text.RegularExpressions.Regex.Match(raw, "```(?:xml|xslt)?\\s*(.*?)```",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return (fenced.Success ? fenced.Groups[1].Value : raw).Trim();
    }

    /// <summary>Log estruturado EXATO do plano (§4) — Source=AiMetrics, campos fixos.
    /// CypressValidado/CStatPollux ficam null nesta etapa (integração futura, fora de escopo).</summary>
    private static void LogCaso(string layout, string modelo, bool sucesso, double tokensPorSegundo,
        int promptChars, double duracaoSegundos, double fewShotSimilarity, double tagOverlap, double textSim,
        bool? xsdValido)
    {
        using (LogContext.PushProperty("Source", "AiMetrics"))
        {
            Serilog.Log.Information(
                "Geracao concluida. Layout={Layout} Modelo={Model} TokensPorSegundo={TokensPerSecond} "
                + "TamanhoPromptChars={PromptChars} DuracaoSegundos={DurationSeconds} "
                + "SimilaridadeFewShot={FewShotSimilarity} TagOverlapRatio={TagOverlapRatio} "
                + "TextSimilarityRatio={TextSimilarityRatio} XsdValido={XsdValid} "
                + "CypressValidado={CypressValidated} CStatPollux={CStatPollux} Sucesso={Sucesso}",
                layout, modelo, tokensPorSegundo, promptChars, duracaoSegundos,
                fewShotSimilarity, tagOverlap, textSim, xsdValido, null, null, sucesso);
        }
    }

    private static void LogResumo(List<CaseResult> resultados, string modelo)
    {
        var ok = resultados.Where(r => r.Sucesso).ToList();
        var falhas = resultados.Count - ok.Count;

        using (LogContext.PushProperty("Source", "AiMetrics"))
        {
            Serilog.Log.Information(
                "Resumo do lote. Modelo={Model} TotalCasos={TotalCasos} Sucesso={Sucesso} Falhas={Falhas} "
                + "TokensPorSegundoMedio={TokensPorSegundoMedio} TagOverlapMedio={TagOverlapMedio} "
                + "TextSimilarityMedia={TextSimilarityMedia}",
                modelo, resultados.Count, ok.Count, falhas,
                ok.Count > 0 ? ok.Average(r => r.TokensPerSecond) : 0,
                ok.Count > 0 ? ok.Average(r => r.TagOverlapRatio) : 0,
                ok.Count > 0 ? ok.Average(r => r.TextSimilarityRatio) : 0);
        }

        Console.WriteLine("");
        Console.WriteLine("── Resumo agregado do lote ────────────────────────────────────────");
        Console.WriteLine($"   Modelo               : {modelo}");
        Console.WriteLine($"   Casos                : {resultados.Count} ({ok.Count} sucesso, {falhas} falha)");
        if (ok.Count > 0)
        {
            Console.WriteLine($"   Throughput médio     : {ok.Average(r => r.TokensPerSecond):F2} tok/s");
            Console.WriteLine($"   Duração média/caso   : {ok.Average(r => r.DurationSeconds):F1}s");
            Console.WriteLine($"   Tag overlap médio    : {ok.Average(r => r.TagOverlapRatio):F3}");
            Console.WriteLine($"   Text similarity média: {ok.Average(r => r.TextSimilarityRatio):F3}");
        }
        Console.WriteLine("   Distribuição por documento:");
        foreach (var g in resultados.GroupBy(r => r.Layout.Split('\\', '/').FirstOrDefault() ?? "?"))
            Console.WriteLine($"      {g.Key,-6} {g.Count(),3} caso(s), {g.Count(r => r.Sucesso),3} sucesso(s)");
    }
}
