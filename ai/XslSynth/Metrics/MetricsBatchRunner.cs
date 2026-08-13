using Serilog;
using Serilog.Context;
using XslSynth.Synthesis;

namespace XslSynth.Metrics;

/// <summary>Opções do modo <c>--mode=metrics-batch</c> (CLI args já resolvidos pelo Program.cs).</summary>
/// <param name="RunDirectory">Diretório do run (contrato Job 1 → Job 2). Null ⇒ não publica
/// artefatos (modo legado: só Serilog). Ver docs/architecture/handoff-job2-cypress-batch.md §2.</param>
/// <param name="RunId">Identificador do run; o wrapper da VM passa o dele para eliminar a
/// corrida de "qual run é o mais recente" entre os dois jobs.</param>
/// <param name="InstancesDirectory">Diretório com TXT de instância REAIS. Sem ele nenhum
/// candidato submissível é produzido — e isso é registrado, não contornado com dado sintético.</param>
/// <param name="NfeXsdPath">XSD do leiaute NF-e para preencher <c>xsdValid</c>. Null ⇒ campo null.</param>
/// <param name="DryRun">Usa o XSLT GABARITO do próprio dataset como candidato, sem chamar o
/// LLM. Serve para exercitar o pipeline de artefatos (run dir, manifesto, XML, elegibilidade)
/// sem gastar horas de inferência — NUNCA para medir o modelo: o manifesto sai com
/// <c>model=dry-run(gabarito)</c> justamente para o resultado ser inconfundível.</param>
public sealed record MetricsBatchOptions(
    string DatasetPath, string Model, int FewShotK, int? Limit, string LogDirectory, string LogFileName,
    string? RunDirectory = null, string? RunId = null, string? InstancesDirectory = null,
    string? NfeXsdPath = null, bool DryRun = false);

/// <summary>Um resultado individual do lote (para o resumo agregado ao final).</summary>
/// <param name="Candidato">XSLT gerado neste caso (null quando não houve saída utilizável) —
/// insumo do run dir do Job 2, não entra em nenhuma métrica.</param>
/// <param name="Prompt">Prompt exato enviado ao Ollama para este caso — insumo do manifesto de
/// auditoria (<see cref="AttemptArtifactWriter"/>), null nos caminhos que não chegam a montar
/// prompt real (dry-run, exceção antes da chamada).</param>
internal sealed record CaseResult(
    string Layout, bool Sucesso, double TokensPerSecond, double DurationSeconds,
    double FewShotSimilarity, double TagOverlapRatio, double TextSimilarityRatio, string? Erro,
    string? Candidato = null, string? Prompt = null, bool WellFormedXml = false,
    bool? XsdValid = null, string? ParseError = null);

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
///
/// Ao final, quando <see cref="MetricsBatchOptions.RunDirectory"/> está configurado, publica
/// o RUN DIR consumido pelo Job 2 (suíte Cypress em modo batch): os XMLs submissíveis em
/// <c>candidates/</c> e o <c>manifest.json</c> por último, em commit atômico. Especificação:
/// docs/architecture/handoff-job2-cypress-batch.md §2.
/// </summary>
public static class MetricsBatchRunner
{
    public static async Task<int> RunAsync(MetricsBatchOptions opts, Action<string> log, CancellationToken ct = default)
    {
        var iniciadoEm = DateTime.UtcNow;

        // ── Guarda de ambiente: recusa rodar como root no Linux ───────────────────────
        // Rodar como root cria logs e artefatos com dono root:root. A rodada agendada (que
        // roda como usuário comum) then falha ao escrever — e o sink de arquivo do Serilog
        // ENGOLE erro de escrita por padrão: a rodada gastaria ~3,6 h sem gravar uma linha e
        // sem erro visível, detectável só dias depois pela ausência de dado no painel.
        // Barato de checar aqui, antes de qualquer escrita (inclusive a do próprio logger).
        if (OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess)
        {
            log("❌ Recusando rodar como root: os artefatos nasceriam root:root e a rodada agendada "
                + "(usuário comum) falharia em SILÊNCIO ao escrever. Rode como o mesmo usuário do cron.");
            return 3;
        }

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
            // O modelo vai por CONSTRUTOR: OllamaClient.Model é somente-leitura e é ele que
            // entra no payload de /api/generate. Antes o --model só chegava ao log — o Ollama
            // gerava com o do env e a série histórica registrava um modelo que não foi usado.
            var client = new OllamaClient(log, model: opts.Model);
            var modelo = opts.DryRun ? "dry-run(gabarito)" : client.Model;

            if (!opts.DryRun && !await client.IsReachableAsync(ct))
            {
                log($"❌ Ollama indisponível em {client.Url} — job de métricas não pode rodar (sem fallback: "
                    + "este modo EXISTE para medir o LLM real, um mock não produziria dado honesto).");
                return 1;
            }
            if (opts.DryRun)
                log("⚠️  DRY-RUN: o candidato de cada caso é o XSLT GABARITO do dataset, não a saída do LLM. "
                    + "Serve para exercitar o run dir/manifesto — NÃO é medição do modelo.");

            var casos = opts.Limit is { } lim && lim > 0 ? pares.Take(lim).ToList() : pares;
            log($"[metrics-batch] modelo={modelo} · few-shot k={opts.FewShotK} · casos nesta rodada={casos.Count}"
                + $" · timeout/caso={client.Timeout.TotalMinutes:F0} min"
                + (opts.Limit is not null ? $" (LIMITADO de {pares.Count} — teste/validação, não rodada completa)" : ""));

            // ── Contrato Job 1 → Job 2: run dir + fábrica de XML submissível ──────────
            var writer = opts.RunDirectory is null ? null
                : new RunArtifactWriter(opts.RunDirectory, opts.RunId ?? RunArtifactWriter.NewRunId(iniciadoEm), log);
            var factory = new CandidateXmlFactory(opts.InstancesDirectory, opts.NfeXsdPath, log);
            // Manifesto de AUDITORIA (issue #35): sobe junto com o run dir, mas é artefato
            // separado do contrato Job1→Job2 — grava TODO candidato (XSLT+prompt+validação),
            // não só os elegíveis ao Pollux. Mesmo diretório de run, para não introduzir um
            // segundo mecanismo de configuração de path (reaproveita --run-dir/LP_METRICS_RUN_DIR).
            var attemptsWriter = opts.RunDirectory is null ? null : new AttemptArtifactWriter(opts.RunDirectory, log);
            if (writer is null)
                log("[metrics-batch] run dir NÃO configurado (--run-dir/LP_METRICS_RUN_DIR ausente) — "
                    + "nenhum artefato para o Job 2 nem manifesto de auditoria será publicado nesta rodada.");
            else
                log($"[metrics-batch] run dir: {writer.RunDirectory} (runId={writer.RunId}) · {factory.DescribeSources()}");
            log("");

            var resultados = new List<CaseResult>();
            var candidatos = new List<ManifestCandidate>();
            var n = 0;
            foreach (var caso in casos)
            {
                n++;
                ct.ThrowIfCancellationRequested();
                log($"[{n}/{casos.Count}] {caso.Id}");

                // ── Resiliência: QUALQUER exceção neste caso é capturada e logada como
                // falha DAQUELE caso — o lote inteiro continua para o próximo. ─────────
                CaseResult resultado;
                try
                {
                    resultado = opts.DryRun
                        ? new CaseResult(caso.Id, true, 0, 0, 0, 1, 1, null, caso.OutputXslt)
                        : await RunCaseAsync(caso, index, client, modelo, opts.FewShotK, log, ct);
                    resultados.Add(resultado);
                }
                catch (Exception ex)
                {
                    log($"   ❌ falha inesperada neste caso (job SEGUE para o próximo): {ex.Message}");
                    LogCaso(caso.Id, modelo, sucesso: false, tokensPorSegundo: 0, promptChars: 0,
                        duracaoSegundos: 0, fewShotSimilarity: 0, tagOverlap: 0, textSim: 0, xsdValido: null);
                    resultados.Add(new CaseResult(caso.Id, false, 0, 0, 0, 0, 0, ex.Message));
                    attemptsWriter?.RecordAttempt(caso.Id, modelo, sucesso: false, erro: ex.Message,
                        xsltGerado: null, promptUsado: null, wellFormedXml: false, tagOverlapRatio: 0,
                        textSimilarityRatio: 0, xsdValid: null, fewShotSimilarity: 0, parseError: null,
                        tokensPorSegundo: 0, duracaoSegundos: 0);
                    continue;
                }

                // ── Manifesto de auditoria: TODA tentativa entra, elegível ao Pollux ou não
                // (issue #35) — falha aqui NUNCA derruba o lote. ───────────────────────────
                try
                {
                    attemptsWriter?.RecordAttempt(caso.Id, modelo, resultado.Sucesso, resultado.Erro,
                        resultado.Candidato, resultado.Prompt, resultado.WellFormedXml,
                        resultado.TagOverlapRatio, resultado.TextSimilarityRatio, resultado.XsdValid,
                        resultado.FewShotSimilarity, resultado.ParseError, resultado.TokensPerSecond,
                        resultado.DurationSeconds);
                }
                catch (Exception ex)
                {
                    log($"   [attempts] falha ao registrar tentativa (lote SEGUE): {ex.Message}");
                }

                // ── Publicação do candidato (XML primeiro; manifesto só no fim) ────────
                // Falha aqui NUNCA derruba o lote: o job de métricas continua valendo
                // mesmo que nenhum candidato vire artefato submissível.
                if (writer is not null && resultado.Candidato is not null)
                {
                    try
                    {
                        var c = PublicaCandidato(caso, resultado.Candidato, factory, writer, log);
                        if (c is not null) candidatos.Add(c);
                    }
                    catch (Exception ex)
                    {
                        log($"   [run-dir] falha ao materializar candidato (lote SEGUE): {ex.Message}");
                    }
                }
            }

            LogResumo(resultados, modelo);

            // ── COMMIT ATÔMICO: manifesto por último. Se o processo morrer antes daqui,
            // o Job 2 não encontra manifesto e não roda — que é exatamente o desejado. ──
            if (writer is not null)
            {
                var manifesto = new RunManifest
                {
                    SchemaVersion = 1,
                    RunId = writer.RunId,
                    StartedAt = RunArtifactWriter.Iso(iniciadoEm),
                    FinishedAt = RunArtifactWriter.Iso(DateTime.UtcNow),
                    Model = modelo,
                    TotalCases = casos.Count,
                    Candidates = candidatos
                };
                writer.TryCommit(manifesto);
                LogResumoCandidatos(candidatos, casos.Count, log);
            }
            attemptsWriter?.TryCommit(writer?.RunId ?? opts.RunId ?? RunArtifactWriter.NewRunId(iniciadoEm),
                iniciadoEm, DateTime.UtcNow, modelo);

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
            // Timeout ≠ modelo sem resposta: o primeiro diz que o teto é curto para ESTE
            // prompt, o segundo é falha real. Confundir os dois vira "o modelo falhou" no
            // painel e distorce a comparação entre modelos.
            var erro = metrics.TimedOut
                ? $"timeout ({metrics.DurationSeconds:F0}s) — caso NÃO concluiu, não é falha de qualidade do modelo"
                : "Ollama sem resposta utilizável";
            log($"   ❌ {erro}");
            LogCaso(caso.Id, modelo, sucesso: false, tokensPorSegundo: metrics.TokensPerSecond,
                promptChars: metrics.PromptChars, duracaoSegundos: metrics.DurationSeconds,
                fewShotSimilarity: similaridadeMedia, tagOverlap: 0, textSim: 0, xsdValido: null);
            return new CaseResult(caso.Id, false, metrics.TokensPerSecond, metrics.DurationSeconds,
                similaridadeMedia, 0, 0, erro, Prompt: prompt);
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
            similaridadeMedia, validacao.TagOverlapRatio, validacao.TextSimilarityRatio, null, candidato,
            Prompt: prompt, WellFormedXml: validacao.WellFormedXml, XsdValid: validacao.XsdValid,
            ParseError: validacao.ParseError);
    }

    /// <summary>
    /// Materializa UM candidato no run dir: aplica o XSLT sobre uma instância real, grava o
    /// XML e devolve a entrada do manifesto. Devolve null quando não há XML — e aí o caso
    /// NÃO entra em <c>candidates[]</c>, garantindo a invariante do contrato de que todo
    /// <c>xmlPath</c> aponta para um arquivo que existe.
    /// </summary>
    private static ManifestCandidate? PublicaCandidato(DatasetPair caso, string candidatoXslt,
        CandidateXmlFactory factory, RunArtifactWriter writer, Action<string> log)
    {
        var operacao = PolluxEligibility.ClassifyOperation(caso.Id);
        var resultado = factory.TryProduce(caso, candidatoXslt, operacao);
        if (resultado.Xml is null)
        {
            log($"   [run-dir] sem XML submissível ({operacao}): {resultado.Motivo}");
            return null;
        }

        // candidateId = layout com separadores trocados por '_'. O campo `layout`, ao
        // contrário, é propagado SEM NENHUMA transformação — é a chave de junção com a API.
        var candidateId = PolluxEligibility.ToCandidateId(caso.Id);
        var xmlPath = writer.TryWriteCandidateXml(candidateId, resultado.Xml);
        if (xmlPath is null) return null;

        log($"   [run-dir] candidato publicado: {candidateId} · elegivel={resultado.Eligible} "
            + $"· fixture={resultado.SourceFixture ?? "-"} · xsdValido={resultado.XsdValid?.ToString() ?? "null"}");

        return new ManifestCandidate
        {
            CandidateId = candidateId,
            Layout = caso.Id,
            DocType = caso.DocType,
            Operation = operacao,
            EligibleForPollux = resultado.Eligible,
            XmlPath = xmlPath,
            SourceFixture = resultado.SourceFixture,
            XsdValid = resultado.XsdValid,
            Notes = resultado.Notes
        };
    }

    /// <summary>Resumo honesto do que o Job 2 vai encontrar — inclusive o caso "nada elegível",
    /// que é estado válido do contrato e precisa ficar explícito no log da rodada.</summary>
    private static void LogResumoCandidatos(List<ManifestCandidate> candidatos, int totalCasos, Action<string> log)
    {
        var elegiveis = candidatos.Count(c => c.EligibleForPollux);
        Console.WriteLine("");
        Console.WriteLine("── Contrato Job 1 → Job 2 (run dir) ───────────────────────────────");
        Console.WriteLine($"   Casos processados      : {totalCasos}");
        Console.WriteLine($"   Candidatos com XML     : {candidatos.Count}");
        Console.WriteLine($"   Elegíveis ao Pollux    : {elegiveis}");
        if (candidatos.Count == 0)
            Console.WriteLine("   (candidates: [] é estado VÁLIDO — o Job 2 trata como skip limpo, não como erro)");
        foreach (var c in candidatos)
            Console.WriteLine($"      {(c.EligibleForPollux ? "✅" : "  ")} {c.CandidateId} "
                + $"[{c.DocType}/{c.Operation}]{(c.Notes is null ? "" : $" — {c.Notes}")}");

        using (LogContext.PushProperty("Source", "AiMetrics"))
        {
            Serilog.Log.Information(
                "Run dir publicado. TotalCasos={TotalCasos} Candidatos={Candidatos} Elegiveis={Elegiveis}",
                totalCasos, candidatos.Count, elegiveis);
        }
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
