using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Xsl;
using XslSynth.Model;

namespace XslSynth.Core;

/// <summary>Relatório do validador de cobertura (sem gabarito de runtime).</summary>
/// <param name="Compiles">O XSLT candidato carrega no XslCompiledTransform?</param>
/// <param name="CompileError">Mensagem do erro de compilação, se houver.</param>
public sealed record CoverageReport(
    bool Compiles, string? CompileError,
    int LinksCovered, int LinksTotal,
    int RulesCovered, int RulesTotal)
{
    public string LinkPct => Pct(LinksCovered, LinksTotal);
    public string RulePct => Pct(RulesCovered, RulesTotal);
    private static string Pct(int a, int b) => b == 0 ? "0%" : $"{(double)a / b * 100:0.#}%";
}

/// <summary>
/// Verificador POSSÍVEL sem gabarito de runtime (o loop completo diff==0 virá quando
/// tivermos os pares input→XML final). Aqui validamos:
///   (a) o candidato é well-formed e COMPILA (XslCompiledTransform.Load);
///   (b) COBERTURA: quantos dos targets de LinkMapping (folha) e de Rule (folha do
///       path T.) aparecem como elementos no XSLT final.
/// </summary>
public sealed class CoverageValidator
{
    public CoverageReport Validate(XDocument candidate, MapperVo mapper)
    {
        var (compiles, error) = TryCompile(candidate);
        var literalNames = LiteralElementNames(candidate);

        var linksCovered = mapper.LinkMappings.Count(l =>
            l.TargetLeafName is { } leaf && literalNames.Contains(SafeName(leaf)));

        var rulesCovered = mapper.Rules.Count(r =>
            RuleLeaf(r) is { } leaf && literalNames.Contains(leaf));

        return new CoverageReport(
            compiles, error,
            linksCovered, mapper.LinkMappings.Count,
            rulesCovered, mapper.Rules.Count);
    }

    private static (bool, string?) TryCompile(XDocument candidate)
    {
        try
        {
            var transform = new XslCompiledTransform();
            using var reader = candidate.CreateReader();
            transform.Load(reader, XsltSettings.Default, null);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Nomes locais de todos os elementos LITERAIS (não-xsl) do candidato.</summary>
    private static HashSet<string> LiteralElementNames(XDocument candidate)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in candidate.Descendants())
            if (el.Name.Namespace != Xslt.Ns)
                names.Add(el.Name.LocalName);
        return names;
    }

    /// <summary>Folha do destino de uma regra (último segmento do path T.).</summary>
    private static string? RuleLeaf(MapperRule r) =>
        string.IsNullOrWhiteSpace(r.TargetPath) ? null : Xslt.Segments(r.TargetPath).LastOrDefault();

    private static string SafeName(string raw)
    {
        var s = Regex.Replace(raw.Trim(), "[^A-Za-z0-9_]", "_");
        if (s.Length == 0) return "_";
        if (!char.IsLetter(s[0]) && s[0] != '_') s = "_" + s;
        return s;
    }
}
