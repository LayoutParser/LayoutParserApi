using System.Xml.Linq;
using Serilog;
using Serilog.Context;
using XslSynth.Core;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace XslSynth.Metrics;

/// <summary>Opções do modo <c>--mode=repair-batch</c> (issue #352 — Fase B do plano de
/// eval/benchmark, docs/architecture/plano-eval-benchmark-ia-2026-09-08.md).</summary>
public sealed record RepairBatchOptions(
    string DatasetPath, string Model, int FewShotK, int? Limit, string LogDirectory, string LogFileName,
    string? InstancesDirectory, string? NfeXsdPath, int MaxIterations = 5);

/// <summary>Resultado de UM caso do lote de convergência real. Público (não interno) para ser
/// testável em isolamento — ver <see cref="RepairBatchRunner.Summarize"/>.</summary>
public sealed record RepairCaseResult(
    string Layout, bool InstanceMatched, bool Converged, int Iterations, int FinalDiffsCount,
    bool? XsdValid, string? Fixture, string? Erro);

/// <summary>Resumo agregado do lote — separado em tipo próprio para ser testável sem depender
/// de captura de stdout/Serilog (issue #352, critério de aceite "resumo agregado ao final").</summary>
public sealed record RepairBatchSummary(
    int TotalCasos, int Medidos, int SemInstancia, int Convergidos,
    double? TaxaConvergenciaReal, double? IteracoesMediasConvergidos);

/// <summary>
/// Fase B do plano de eval/benchmark (issue #352, Gap G3): eleva o critério de "acertou" do
/// benchmark de similaridade tolerante (<see cref="OutputValidator"/>, usado pelo
/// <c>--mode=metrics-batch</c>) para o critério REAL que decide correção fiscal em produção —
/// diff==0 (<see cref="CanonicalDiffer"/>) + XSD válido — rodando o mesmo tipo de loop que
/// <see cref="RepairOrchestrator"/> usa no fluxo <c>--generate</c>, só que em LOTE contra o
/// dataset held-out, por modelo.
///
/// LIMITAÇÃO HONESTA (documentada, não escondida): <see cref="RepairOrchestrator.RunAsync"/> não
/// pôde ser reaproveitado tal como está — ele exige um <see cref="MapperVo"/> (LinkMappings/Rules
/// estruturados) + XML de INSTÂNCIA real de entrada, e o dataset held-out
/// (<c>dataset_pairs_filtered_v2.jsonl</c>) só tem pares (schema TCL de texto, XSLT-alvo de
/// texto) — não tem MapperVO nem instância de entrada pareada (ver
/// docs/architecture/plano-eval-benchmark-ia-2026-09-08.md, achado "não vamos reconstruir" +
/// memória finetuning-poc-fase1-dataset.md). Diff==0 real só é COMPUTÁVEL quando existe, em
/// <see cref="RepairBatchOptions.InstancesDirectory"/>, um TXT de instância que estruturalmente
/// case com o schema TCL do caso (via <see cref="TclRootBuilder"/> — mesmo mecanismo do
/// <see cref="CandidateXmlFactory"/>, mas SEM o filtro de elegibilidade Pollux: aqui o objetivo é
/// medir convergência técnica, não submissibilidade). Hoje isso cobre uma fração pequena dos 54
/// pares (principalmente NFe, ver Examples/ no servidor) — os demais casos entram no relatório
/// como "sem instância" (não contam nem a favor nem contra a taxa de convergência, para não
/// produzir falso-negativo sistemático). A taxa de convergência reportada é sobre os casos
/// MEDIDOS, não sobre os 54 — isso fica explícito no resumo agregado.
/// </summary>
public static class RepairBatchRunner
{
    public static async Task<int> RunAsync(RepairBatchOptions opts, Action<string> log, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess)
        {
            log("❌ Recusando rodar como root (mesmo motivo do metrics-batch — ver MetricsBatchRunner).");
            return 3;
        }

        if (!File.Exists(opts.DatasetPath))
        {
            log($"❌ Dataset não encontrado: {opts.DatasetPath}");
            return 2;
        }

        Directory.CreateDirectory(opts.LogDirectory);
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Src:{Source}] [Corr:{CorrelationId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(opts.LogDirectory, opts.LogFileName),
                rollingInterval: Serilog.RollingInterval.Day,
                rollOnFileSizeLimit: true,
                outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Src:{Source}] [Corr:{CorrelationId}] {Message:lj}{NewLine}{Exception}",
                shared: true)
            .CreateLogger();

        try
        {
            var pares = DatasetPair.Load(opts.DatasetPath, log);
            log($"[repair-batch] dataset carregado: {pares.Count} pares.");
            if (pares.Count == 0)
            {
                log("❌ Dataset vazio ou 100% ilegível — nada a rodar.");
                return 1;
            }

            var index = DatasetFewShotIndex.Build(pares);
            var client = new OllamaClient(log, model: opts.Model);
            var synthesizer = new OllamaXslSynthesizer(log);
            var modelo = client.Model;

            if (!await client.IsReachableAsync(ct))
            {
                log($"❌ Ollama indisponível em {client.Url} — repair-batch não pode rodar (mede o LLM real, sem fallback mock).");
                return 1;
            }

            var instancias = opts.InstancesDirectory is not null && Directory.Exists(opts.InstancesDirectory)
                ? Directory.EnumerateFiles(opts.InstancesDirectory)
                    .Where(f => !f.EndsWith(".gitkeep", StringComparison.Ordinal))
                    .OrderBy(f => f, StringComparer.Ordinal).ToList()
                : new List<string>();
            log($"[repair-batch] instâncias disponíveis: {instancias.Count}"
                + (instancias.Count == 0 ? " — NENHUM caso terá diff==0 real computável (todos entram como 'sem instância')." : ""));

            var casos = opts.Limit is { } lim && lim > 0 ? pares.Take(lim).ToList() : pares;
            log($"[repair-batch] modelo={modelo} · few-shot k={opts.FewShotK} · max iterações/caso={opts.MaxIterations} "
                + $"· casos nesta rodada={casos.Count}"
                + (opts.Limit is not null ? $" (LIMITADO de {pares.Count})" : ""));
            log("");

            var resultados = new List<RepairCaseResult>();
            var n = 0;
            foreach (var caso in casos)
            {
                n++;
                ct.ThrowIfCancellationRequested();
                log($"[{n}/{casos.Count}] {caso.Id}");

                RepairCaseResult resultado;
                try
                {
                    resultado = await RunCaseAsync(caso, index, client, synthesizer, opts, log, ct);
                }
                catch (Exception ex)
                {
                    log($"   ❌ falha inesperada neste caso (lote SEGUE): {ex.Message}");
                    resultado = new RepairCaseResult(caso.Id, false, false, 0, -1, null, null, ex.Message);
                }

                resultados.Add(resultado);
                LogCaso(resultado, modelo);
            }

            LogResumo(resultados, modelo);
            return resultados.Any(r => r.InstanceMatched) ? 0 : 1;
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    private static async Task<RepairCaseResult> RunCaseAsync(DatasetPair caso, DatasetFewShotIndex index,
        OllamaClient client, OllamaXslSynthesizer synthesizer, RepairBatchOptions opts, Action<string> log, CancellationToken ct)
    {
        var map = TclLayoutMap.TryParse(caso.InputMapTcl);
        if (map is null)
        {
            log("   [repair-batch] schema TCL do par ilegível ou sem <LINE> — sem instância possível.");
            return new RepairCaseResult(caso.Id, false, false, 0, -1, null, null, "schema TCL ilegível");
        }

        var instanciasDisponiveis = opts.InstancesDirectory is not null && Directory.Exists(opts.InstancesDirectory)
            ? Directory.EnumerateFiles(opts.InstancesDirectory).Where(f => !f.EndsWith(".gitkeep", StringComparison.Ordinal)).ToList()
            : new List<string>();
        var (root, fixture, motivo) = ResolveInstancia(caso.Id, map, instanciasDisponiveis);
        if (root is null)
        {
            log($"   [repair-batch] sem instância real compatível — caso não entra na taxa de convergência ({motivo}).");
            return new RepairCaseResult(caso.Id, false, false, 0, -1, null, null, motivo);
        }

        XDocument gabaritoXslt;
        try { gabaritoXslt = XDocument.Parse(caso.OutputXslt); }
        catch (Exception ex)
        {
            log($"   [repair-batch] XSLT gabarito do dataset não é XML bem-formado — caso descartado: {ex.Message}");
            return new RepairCaseResult(caso.Id, false, false, 0, -1, null, fixture, "gabarito malformado: " + ex.Message);
        }

        var applier = new XsltApplier();
        string expectedOutput;
        try { expectedOutput = applier.Apply(gabaritoXslt, root); }
        catch (Exception ex)
        {
            log($"   [repair-batch] gabarito não aplicou sobre a instância '{fixture}' — caso descartado: {ex.Message}");
            return new RepairCaseResult(caso.Id, false, false, 0, -1, null, fixture, "gabarito não aplicou: " + ex.Message);
        }

        log($"   instância casada: {fixture}");

        var recuperados = index.Retrieve(caso, opts.FewShotK);
        var prompt = MetricsBatchRunner.BuildPrompt(caso, recuperados);
        var (respostaBruta, metrics) = await client.GenerateWithMetricsAsync(prompt, ct);
        if (!metrics.Success || string.IsNullOrWhiteSpace(respostaBruta))
        {
            log("   ❌ Ollama sem resposta utilizável na geração inicial.");
            return new RepairCaseResult(caso.Id, true, false, 0, -1, null, fixture, "sem resposta do LLM");
        }

        var candidatoTexto = MetricsBatchRunner.ExtractXml(respostaBruta);
        var briefing = new SynthesisBriefing { Mapper = new MapperVo(), TargetRootName = root.Root?.Name.LocalName ?? "ROOT" };

        var differ = new CanonicalDiffer();
        var iterations = 0;
        IReadOnlyList<NodeDiff> diffs = [new NodeDiff("nao-avaliado", "/", null, null)];
        bool? xsdValid = null;

        while (iterations < opts.MaxIterations)
        {
            iterations++;

            string atual;
            try
            {
                var xslt = XDocument.Parse(candidatoTexto);
                atual = applier.Apply(xslt, root);
                diffs = differ.Diff(expectedOutput, atual);
                xsdValid = opts.NfeXsdPath is not null ? ValidaXsdNfe(atual, opts.NfeXsdPath, log) : null;
            }
            catch (Exception ex)
            {
                // XSLT candidato não compila/não aplica: trata como divergência total,
                // repassa o erro para o LLM consertar na próxima iteração (mesmo espírito
                // do Passo 6 do RepairOrchestrator — nenhuma saída inválida é aceita).
                diffs = [new NodeDiff("erro-aplicacao", "/", null, ex.Message)];
                xsdValid = null;
            }

            log($"   iteração {iterations}/{opts.MaxIterations}: diffs={diffs.Count} · xsdValido={(xsdValid?.ToString() ?? "null")}");

            if (diffs.Count == 0 && xsdValid != false)
                break;

            if (iterations >= opts.MaxIterations) break;
            candidatoTexto = await synthesizer.RepairFromDiffAsync(candidatoTexto, diffs, briefing, ct);
        }

        var converged = diffs.Count == 0 && xsdValid != false;
        log(converged ? "   ✅ CONVERGIU (diff==0 + XSD válido/indisponível)." : $"   ❌ não convergiu ({diffs.Count} diff(s) residual(is)).");
        return new RepairCaseResult(caso.Id, true, converged, iterations, diffs.Count, xsdValid, fixture, null);
    }

    /// <summary>Mesma lógica de casamento do <see cref="CandidateXmlFactory"/>, mas SEM o
    /// filtro de elegibilidade Pollux — aqui qualquer docType/operação com instância real
    /// compatível entra na medição de convergência técnica.</summary>
    private static (XDocument? Root, string? Fixture, string Motivo) ResolveInstancia(
        string layoutId, TclLayoutMap map, IReadOnlyList<string> instancias)
    {
        if (instancias.Count == 0)
            return (null, null, "nenhum diretório de instâncias configurado/existente");

        var stem = layoutId.Split('\\', '/').LastOrDefault() ?? layoutId;
        var ordenadas = instancias
            .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Contains(stem, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var melhorTaxa = 0.0;
        string? melhorMotivo = null;
        foreach (var arquivo in ordenadas)
        {
            var r = TclRootBuilder.TryBuild(arquivo, map);
            if (r.Root is not null) return (r.Root, Path.GetFileName(arquivo), "");
            if (r.TaxaCasamento >= melhorTaxa)
            {
                melhorTaxa = r.TaxaCasamento;
                melhorMotivo = $"{Path.GetFileName(arquivo)}: {r.Motivo}";
            }
        }

        return (null, null,
            $"nenhuma das {instancias.Count} instância(s) casa com o schema TCL deste par "
            + $"(melhor casamento {melhorTaxa:P0}) — {melhorMotivo}");
    }

    /// <summary>Oráculo XSD do leiaute NF-e (mesmo escopo do <see cref="CandidateXmlFactory"/>):
    /// só se aplica quando a saída tem forma de NF-e; fora disso fica null, nunca false por
    /// ausência de cobertura.</summary>
    private static bool? ValidaXsdNfe(string xmlSaida, string nfeXsdPath, Action<string> log)
    {
        if (!File.Exists(nfeXsdPath)) return null;
        try
        {
            var raiz = XDocument.Parse(xmlSaida).Root;
            if (raiz is null || raiz.Name.LocalName != "NFe") return null; // fora do escopo do oráculo

            XNamespace ns = PolluxEligibility.NfeNamespace;
            var comNs = raiz.Name.NamespaceName == ns.NamespaceName ? raiz : ComNamespace(raiz, ns);
            var res = new XsdValidator().Validate(comNs.ToString(), nfeXsdPath);
            var reais = res.Errors.Count(e => !e.Contains("Signature", StringComparison.Ordinal));
            return reais == 0;
        }
        catch (Exception ex)
        {
            log($"   [xsd] validação indisponível para esta iteração ({ex.Message}) — xsdValid=null.");
            return null;
        }
    }

    private static XElement ComNamespace(XElement el, XNamespace ns)
    {
        var novo = new XElement(ns + el.Name.LocalName);
        foreach (var a in el.Attributes().Where(a => !a.IsNamespaceDeclaration))
            novo.Add(new XAttribute(a.Name.LocalName, a.Value));
        foreach (var n in el.Nodes())
            novo.Add(n is XElement c ? ComNamespace(c, ns) : n);
        return novo;
    }

    /// <summary>Log estruturado por caso — Source=AiMetrics (mesma série do metrics-batch),
    /// campos NOVOS de convergência real (issue #352): Converged/Iterations/FinalDiffsCount/
    /// XsdValidoReal. InstanceMatched=false ⇒ caso não entrou na taxa (sem dado de instância).</summary>
    private static void LogCaso(RepairCaseResult r, string modelo)
    {
        using (LogContext.PushProperty("Source", "AiMetrics"))
        {
            Serilog.Log.Information(
                "Convergencia real avaliada (repair-batch). Layout={Layout} Modelo={Model} "
                + "InstanceMatched={InstanceMatched} Converged={Converged} Iterations={Iterations} "
                + "FinalDiffsCount={FinalDiffsCount} XsdValidoReal={XsdValidoReal} Fixture={Fixture} Erro={Erro}",
                r.Layout, modelo, r.InstanceMatched, r.Converged, r.Iterations, r.FinalDiffsCount,
                r.XsdValid, r.Fixture, r.Erro);
        }
    }

    /// <summary>Agregação PURA (sem I/O) — testável em isolamento a partir de uma lista de
    /// <see cref="RepairCaseResult"/> simulados. É o coração do critério de aceite do #352:
    /// taxa de convergência real (%) por modelo, sobre os casos MEDIDOS (com instância real),
    /// nunca sobre o total do dataset (evita falso-negativo sistemático nos casos sem instância).</summary>
    public static RepairBatchSummary Summarize(IReadOnlyList<RepairCaseResult> resultados)
    {
        var medidos = resultados.Where(r => r.InstanceMatched).ToList();
        var convergidos = medidos.Count(r => r.Converged);
        var semInstancia = resultados.Count - medidos.Count;
        var taxaConvergencia = medidos.Count > 0 ? (double)convergidos / medidos.Count : (double?)null;
        var convergidosLista = medidos.Where(r => r.Converged).ToList();
        var iteracoesMedias = convergidosLista.Count > 0 ? convergidosLista.Average(r => r.Iterations) : (double?)null;

        return new RepairBatchSummary(resultados.Count, medidos.Count, semInstancia, convergidos,
            taxaConvergencia, iteracoesMedias);
    }

    private static void LogResumo(List<RepairCaseResult> resultados, string modelo)
    {
        var s = Summarize(resultados);

        using (LogContext.PushProperty("Source", "AiMetrics"))
        {
            Serilog.Log.Information(
                "Resumo do lote de convergencia real (repair-batch). Modelo={Model} TotalCasos={TotalCasos} "
                + "Medidos={Medidos} SemInstancia={SemInstancia} Convergidos={Convergidos} "
                + "TaxaConvergenciaReal={TaxaConvergenciaReal} IteracoesMediasConvergidos={IteracoesMediasConvergidos}",
                modelo, s.TotalCasos, s.Medidos, s.SemInstancia, s.Convergidos,
                s.TaxaConvergenciaReal, s.IteracoesMediasConvergidos);
        }

        Console.WriteLine("");
        Console.WriteLine("── Resumo agregado — taxa de convergência REAL (diff==0 + XSD) ────");
        Console.WriteLine($"   Modelo                       : {modelo}");
        Console.WriteLine($"   Casos no lote                : {s.TotalCasos}");
        Console.WriteLine($"   Sem instância (não medidos)  : {s.SemInstancia}"
            + (s.SemInstancia == s.TotalCasos ? "  ⚠️ NENHUM caso medido — configure --instances." : ""));
        Console.WriteLine($"   Medidos (com instância real) : {s.Medidos}");
        if (s.Medidos > 0)
        {
            Console.WriteLine($"   Convergiram (diff==0+XSD)    : {s.Convergidos}/{s.Medidos} ({s.TaxaConvergenciaReal:P1})");
            Console.WriteLine($"   Iterações médias (convergiu) : {(s.IteracoesMediasConvergidos is { } im ? im.ToString("F1") : "n/a")}");
        }
        Console.WriteLine("   ⚠️ Taxa calculada sobre os casos MEDIDOS, não sobre o total do dataset —");
        Console.WriteLine("      diff==0 real exige instância TXT compatível (ver limitação documentada em RepairBatchRunner.cs).");
    }
}
