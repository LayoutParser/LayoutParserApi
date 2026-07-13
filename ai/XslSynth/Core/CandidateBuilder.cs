using System.Text.RegularExpressions;
using System.Xml.Linq;
using XslSynth.Synthesis;

namespace XslSynth.Core;

/// <summary>Métricas da montagem do candidato.</summary>
/// <param name="RuleNodes">Nós de destino de regra inseridos.</param>
/// <param name="LinkLeaves">Folhas de LinkMapping inseridas.</param>
/// <param name="VarsDeclared">Variáveis declaradas no topo (referenciadas pelos corpos).</param>
public sealed record CandidateStats(int RuleNodes, int LinkLeaves, int VarsDeclared);

/// <summary>
/// Combina a saída determinística (folhas dos 237 LinkMappings) com os fragmentos
/// das Rules traduzidas (DSL→XSLT) num ÚNICO stylesheet candidato.
///
/// • Rules: cada uma é ancorada no seu caminho <c>T.</c> real (árvore NF-e reconstruída).
/// • LinkMappings: agrupados num container <c>lp_LinkMappings</c> (o pai real depende
///   do catálogo GUID→path, ainda ausente — ver <see cref="LinkMappingTranspiler"/>).
/// • Variáveis (<c>$var</c>) referenciadas pelos corpos são declaradas no topo para
///   o candidato COMPILAR mesmo sem o loop de reparo por gabarito.
/// </summary>
public sealed class CandidateBuilder
{
    private static readonly Regex VarRef = new(@"\$([A-Za-z_][A-Za-z0-9_.\-]*)", RegexOptions.Compiled);

    public (XDocument Xslt, CandidateStats Stats) Build(
        string rootName,
        IReadOnlyList<XElement> linkLeaves,
        IReadOnlyList<RuleTranslation> ruleTranslations)
    {
        var literalRoot = new XElement(rootName);

        // ── Rules: ancora cada regra no seu path T. real ─────────────────────
        var ruleNodes = 0;
        foreach (var tr in ruleTranslations)
        {
            if (string.IsNullOrWhiteSpace(tr.TargetPath)) continue;
            var segs = Xslt.Segments(tr.TargetPath);
            if (segs.Length == 0) continue;

            XElement leaf;
            if (segs[0] == rootName)
            {
                leaf = DeterministicXslTranspiler.GetOrCreateChildPath(literalRoot, segs);
            }
            else
            {
                // Path que não começa na raiz esperada: cria sob a raiz mesmo assim.
                leaf = DeterministicXslTranspiler.GetOrCreateChildPath(
                    literalRoot, new[] { rootName }.Concat(segs).ToArray());
            }

            leaf.Add(new XComment($" rule='{tr.Rule.Name}' src={tr.Source} "));
            foreach (var node in XsltFragment.ParseNodes(tr.BodyXsl))
                leaf.Add(node);
            ruleNodes++;
        }

        // ── LinkMappings: container dedicado (path do pai pendente de catálogo) ─
        var linkContainer = new XElement("lp_LinkMappings",
            new XComment(" 237 LinkMappings: destino resolvido só ate a folha; " +
                         "pai/input dependem do catalogo GUID->path (ausente) "));
        foreach (var leaf in linkLeaves)
            linkContainer.Add(leaf);
        literalRoot.Add(linkContainer);

        // ── Declara as variáveis referenciadas (para compilar) ───────────────
        var referenced = CollectVars(literalRoot);
        var varDecls = referenced
            .Select(v => new XElement(Xslt.Ns + "variable",
                new XAttribute("name", v), new XAttribute("select", "''")))
            .Cast<object>()
            .ToArray();

        var doc = new XDocument(
            new XElement(Xslt.Ns + "stylesheet",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsl", Xslt.Ns.NamespaceName),
                new XElement(Xslt.Ns + "output",
                    new XAttribute("method", "xml"), new XAttribute("indent", "yes")),
                varDecls,
                new XElement(Xslt.Ns + "template",
                    new XAttribute("match", "/"),
                    literalRoot)));

        return (doc, new CandidateStats(ruleNodes, linkLeaves.Count, referenced.Count));
    }

    /// <summary>Coleta nomes de variáveis referenciadas (<c>$nome</c>) em toda a árvore.</summary>
    private static IReadOnlyCollection<string> CollectVars(XElement tree)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attr in tree.Descendants().Attributes())
            foreach (Match m in VarRef.Matches(attr.Value))
                names.Add(XsltFragment.SanitizeVar(m.Groups[1].Value));
        return names;
    }
}
