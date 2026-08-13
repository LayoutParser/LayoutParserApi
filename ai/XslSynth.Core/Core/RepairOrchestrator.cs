using System.Xml.Linq;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace XslSynth.Core;

/// <summary>Relatório final do loop de síntese.</summary>
public sealed record SynthesisReport(
    bool Converged,
    int Iterations,
    int MappedFields,
    int TotalFields,
    string FinalXslt,
    string FinalOutput,
    IReadOnlyList<NodeDiff> FinalDiffs,
    XsdResult FinalXsd);

/// <summary>
/// O LOOP FECHADO (passos 2→6): transpilar → (LLM completa) → aplicar → diff/XSD →
/// (LLM conserta) → repete até diff==0 e XSD válido, ou esgotar as iterações.
/// Nenhuma saída do LLM é aceita sem passar pelo verificador determinístico.
/// </summary>
public sealed class RepairOrchestrator
{
    private readonly DeterministicXslTranspiler _transpiler = new();
    private readonly XsltApplier _applier = new();
    private readonly CanonicalDiffer _differ = new();
    private readonly XsdValidator _xsd = new();

    public async Task<SynthesisReport> RunAsync(
        MapperVo mapper,
        XDocument input,
        string expectedXml,
        string xsdPath,
        IXslSynthesizer synthesizer,
        Action<string> log,
        int maxIterations = 5,
        CancellationToken ct = default)
    {
        var totalFields = mapper.LinkMappings.Count + mapper.Rules.Count;

        // ── Passo 2: baseline determinístico (sem IA) ────────────────────────
        var baseline = _transpiler.Transpile(mapper);
        var xslt = baseline.Xslt;

        log("== Baseline (transpilador determinístico) ==");
        log($"   Campos diretos transpilados: {baseline.MappedFields}/{totalFields} " +
            $"({Pct(baseline.MappedFields, totalFields)})");

        var (output, diffs, xsd) = Evaluate(xslt, input, expectedXml, xsdPath);
        Report(log, output, diffs, xsd);

        var briefing = new SynthesisBriefing
        {
            Mapper = mapper,
            TargetRootName = baseline.RootName,
            SeedXsl = mapper.XslContent
        };

        var iterations = 0;
        var rulesSynthesized = false;

        while ((diffs.Count > 0 || !xsd.IsValid) && iterations < maxIterations)
        {
            iterations++;

            if (!rulesSynthesized)
            {
                // ── Passo 3: LLM traduz as Rules C# e completa os buracos ────
                log($"== Iteração {iterations}: síntese de regras ({synthesizer.Name}) ==");
                var fragments = await synthesizer.SynthesizeRulesAsync(briefing, ct);
                rulesSynthesized = true;

                foreach (var frag in fragments)
                {
                    MergeFragment(xslt, frag, log);
                    log($"   + regra '{frag.RuleName}' → {frag.TargetParentPath}");
                }
            }
            else
            {
                // ── Passo 6: LLM conserta a partir dos diffs residuais ───────
                log($"== Iteração {iterations}: reparo por diff ({synthesizer.Name}) ==");
                var repairedText = await synthesizer.RepairFromDiffAsync(
                    xslt.ToString(SaveOptions.DisableFormatting), diffs, briefing, ct);
                xslt = XDocument.Parse(repairedText);
            }

            // ── Passos 4+5: aplicar e verificar ──────────────────────────────
            (output, diffs, xsd) = Evaluate(xslt, input, expectedXml, xsdPath);
            Report(log, output, diffs, xsd);
        }

        var converged = diffs.Count == 0 && xsd.IsValid;
        return new SynthesisReport(
            converged, iterations, baseline.MappedFields, totalFields,
            xslt.ToString(), output, diffs, xsd);
    }

    private (string Output, IReadOnlyList<NodeDiff> Diffs, XsdResult Xsd) Evaluate(
        XDocument xslt, XDocument input, string expectedXml, string xsdPath)
    {
        var output = _applier.Apply(xslt, input);
        var diffs = _differ.Diff(expectedXml, output);
        var xsd = _xsd.Validate(output, xsdPath);
        return (output, diffs, xsd);
    }

    private static void Report(Action<string> log, string output, IReadOnlyList<NodeDiff> diffs, XsdResult xsd)
    {
        log($"   Diffs: {diffs.Count} | XSD: {(xsd.IsValid ? "válido" : "INVÁLIDO")}");
        foreach (var d in diffs)
            log("     " + d);
        if (!xsd.IsValid)
            foreach (var e in xsd.Errors)
                log("     [xsd] " + e);
    }

    /// <summary>Insere o fragmento de regra na árvore XSLT, no pai indicado pelo XPath.</summary>
    private static void MergeFragment(XDocument xslt, RuleFragment frag, Action<string> log)
    {
        var template = xslt.Root!.Element(Xslt.Ns + "template");
        var literalRoot = template?.Elements().FirstOrDefault(e => e.Name.Namespace != Xslt.Ns);
        if (literalRoot is null)
        {
            log("   ! árvore XSLT sem raiz literal — fragmento ignorado.");
            return;
        }

        var segs = Xslt.Segments(frag.TargetParentPath);
        if (segs.Length == 0 || segs[0] != literalRoot.Name.LocalName)
        {
            log($"   ! caminho '{frag.TargetParentPath}' não bate com a raiz '{literalRoot.Name.LocalName}'.");
            return;
        }

        var parent = DeterministicXslTranspiler.GetOrCreateChildPath(literalRoot, segs);
        parent.Add(XElement.Parse(frag.LeafXsl));
    }

    private static string Pct(int part, int total) =>
        total == 0 ? "0%" : $"{(double)part / total * 100:0.#}%";
}
