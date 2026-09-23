using System.Text.RegularExpressions;
using System.Xml.Linq;
using XslSynth.Synthesis;

namespace XslSynth.Core;

/// <summary>Métricas da montagem do candidato.</summary>
/// <param name="RuleNodes">Nós de destino de regra inseridos.</param>
/// <param name="LinkLeaves">Folhas de LinkMapping inseridas.</param>
/// <param name="VarsDeclared">Variáveis declaradas no topo (referenciadas pelos corpos).</param>
/// <param name="Shell">Casca do documento efetivamente emitida (issue #438).</param>
/// <param name="Limitations">O que a casca NÃO conseguiu determinar com segurança (nunca chutado) — vai pro relatório.</param>
public sealed record CandidateStats(
    int RuleNodes, int LinkLeaves, int VarsDeclared,
    ShellInfo? Shell = null, IReadOnlyList<string>? Limitations = null);

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
        IReadOnlyList<RuleTranslation> ruleTranslations,
        DocumentShellOptions? shell = null)
    {
        shell ??= DocumentShellOptions.Empty;
        var literalRoot = new XElement(rootName);
        var limitations = new List<string>();
        var attributesEmitted = new List<string>();
        string? documentNamespace = null;

        // ── Rules: ancora cada regra no seu path T. real ─────────────────────
        var ruleNodes = 0;
        foreach (var tr in ruleTranslations)
        {
            if (string.IsNullOrWhiteSpace(tr.TargetPath)) continue;
            var segs = Xslt.Segments(tr.TargetPath);
            if (segs.Length == 0) continue;

            // ── Casca (issue #438): atributo/namespace NÃO viram elemento-filho ─────
            if (shell.IsAttribute(tr))
            {
                var attrName = segs[^1];
                var parentSegs = segs[0] == rootName ? segs[..^1] : new[] { rootName }.Concat(segs[..^1]).ToArray();
                var owner = DeterministicXslTranspiler.GetOrCreateChildPath(literalRoot, parentSegs);

                if (DocumentShellOptions.IsNamespaceDeclaration(attrName))
                {
                    if (attrName != "xmlns")
                    {
                        limitations.Add($"Declaração de namespace com prefixo '{attrName}' (regra '{tr.Rule.Name}') não suportada: descartada, não virou elemento.");
                    }
                    else if (ConstantText(tr.BodyXsl) is { } ns && parentSegs.Length <= 1)
                    {
                        documentNamespace = ns;
                    }
                    else
                    {
                        limitations.Add($"Namespace do documento (xmlns, regra '{tr.Rule.Name}') não é constante ou não está na raiz: a casca sai SEM namespace.");
                    }
                    continue;
                }

                if (!IsSafeAttributeName(attrName))
                {
                    limitations.Add($"Atributo '{attrName}' (regra '{tr.Rule.Name}') tem nome inválido para XML: descartado.");
                    continue;
                }

                var attr = new XElement(Xslt.Ns + "attribute", new XAttribute("name", attrName));
                foreach (var node in XsltFragment.ParseNodes(tr.BodyXsl))
                    attr.Add(node);
                InsertAttributeFirst(owner, attr);
                attributesEmitted.Add(string.Join('/', parentSegs.Append(attrName)));
                ruleNodes++;
                continue;
            }

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

        // ── Namespace do documento: TODOS os literais sem namespace passam a ele ───────
        // (senão o serializador emitiria xmlns="" nos filhos e o documento perderia o namespace).
        if (documentNamespace is not null)
        {
            var ns = XNamespace.Get(documentNamespace);
            foreach (var el in literalRoot.DescendantsAndSelf().Where(e => e.Name.Namespace == XNamespace.None).ToList())
                el.Name = ns + el.Name.LocalName;
        }

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

        return (doc, new CandidateStats(
            ruleNodes, linkLeaves.Count, referenced.Count,
            new ShellInfo(rootName, documentNamespace, attributesEmitted), limitations));
    }

    /// <summary>
    /// Texto constante de um corpo <c>&lt;xsl:text&gt;valor&lt;/xsl:text&gt;</c> (único nó, sem xsl:if/value-of).
    /// null = corpo condicional/dinâmico — a casca não pode assumir o valor.
    /// </summary>
    internal static string? ConstantText(string bodyXsl)
    {
        try
        {
            var nodes = XsltFragment.ParseNodes(bodyXsl).Where(n => !(n is XText t && string.IsNullOrWhiteSpace(t.Value))).ToList();
            if (nodes.Count == 1 && nodes[0] is XElement { Name: var n } el && n == Xslt.Ns + "text"
                && !el.HasElements && !string.IsNullOrWhiteSpace(el.Value))
                return el.Value.Trim();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeAttributeName(string name)
    {
        try { System.Xml.XmlConvert.VerifyNCName(name); return true; }
        catch { return false; }
    }

    /// <summary>
    /// xsl:attribute precisa vir ANTES de qualquer nó-filho do elemento (senão erro em runtime XSLT).
    /// Insere depois dos xsl:attribute já presentes, antes do resto.
    /// </summary>
    private static void InsertAttributeFirst(XElement owner, XElement attr)
    {
        var lastAttr = owner.Elements().LastOrDefault(e => e.Name == Xslt.Ns + "attribute");
        if (lastAttr is not null) lastAttr.AddAfterSelf(attr);
        else owner.AddFirst(attr);
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
