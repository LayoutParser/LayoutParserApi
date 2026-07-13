using System.Text.RegularExpressions;
using XslSynth.Core;
using XslSynth.Model;

namespace XslSynth.Synthesis;

/// <summary>
/// Sintetizador DETERMINÍSTICO — permite provar o loop ponta-a-ponta 100% OFFLINE
/// (sem Ollama). NÃO "chuta" o resultado: traduz de fato o C# da Rule para XSLT
/// usando um mini-tradutor de um padrão conhecido (ternário de comparação).
/// É um stand-in fiel do que o LLM faz — apenas com escopo restrito ao padrão do demo.
/// </summary>
public sealed class MockXslSynthesizer : IXslSynthesizer
{
    public string Name => "Mock (determinístico, offline)";

    // Casa: return Number("<xpath>") <op> <n> ? "<A>" : "<B>";
    private static readonly Regex TernaryComparison = new(
        """return\s+Number\(\s*"(?<xpath>[^"]+)"\s*\)\s*(?<op>>=|<=|==|>|<)\s*(?<num>[0-9]+(?:\.[0-9]+)?)\s*\?\s*"(?<a>[^"]*)"\s*:\s*"(?<b>[^"]*)"\s*;""",
        RegexOptions.Compiled);

    public Task<IReadOnlyList<RuleFragment>> SynthesizeRulesAsync(
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

            var inner = TranslateCSharp(rule.ContentValue!);
            var leafXsl =
                $"<{leafName} xmlns:xsl=\"{Xslt.Ns.NamespaceName}\">{inner}</{leafName}>";

            fragments.Add(new RuleFragment(rule.Name ?? leafName, parentPath, leafXsl));
        }

        return Task.FromResult<IReadOnlyList<RuleFragment>>(fragments);
    }

    // O Mock não usa o canal de reparo por diff: a tradução da Rule já fecha o diff.
    // (O canal existe para o LLM real — ver OllamaXslSynthesizer.RepairFromDiffAsync.)
    public Task<string> RepairFromDiffAsync(
        string currentXsl, IReadOnlyList<NodeDiff> diffs, SynthesisBriefing briefing, CancellationToken ct = default)
        => Task.FromResult(currentXsl);

    /// <summary>Mini-tradutor C#→XSLT do padrão suportado (ternário de comparação).</summary>
    private static string TranslateCSharp(string csharp)
    {
        // Remove comentários de linha para simplificar o casamento.
        var code = Regex.Replace(csharp, "//.*", string.Empty);

        var m = TernaryComparison.Match(code);
        if (!m.Success)
        {
            // Padrão não suportado pelo Mock → devolve comentário (o LLM real cobriria).
            return "<xsl:comment> regra nao traduzida pelo mock </xsl:comment>";
        }

        var xpath = m.Groups["xpath"].Value;
        var op = EscapeOp(m.Groups["op"].Value);
        var num = m.Groups["num"].Value;
        var a = m.Groups["a"].Value;
        var b = m.Groups["b"].Value;

        return $"<xsl:choose>" +
               $"<xsl:when test=\"number({xpath}) {op} {num}\">{a}</xsl:when>" +
               $"<xsl:otherwise>{b}</xsl:otherwise>" +
               $"</xsl:choose>";
    }

    private static string EscapeOp(string op) => op switch
    {
        ">" => "&gt;",
        "<" => "&lt;",
        ">=" => "&gt;=",
        "<=" => "&lt;=",
        "==" => "=",
        _ => op
    };
}
