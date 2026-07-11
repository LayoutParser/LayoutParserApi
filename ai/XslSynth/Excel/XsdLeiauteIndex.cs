using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// XsdLeiauteIndex — caminha o leiauteNFe_v4.00.xsd em ORDEM DE DOCUMENTO e
// materializa cada nó (elemento, grupo, atributo) com XPath, tipo, ocorrência
// e a xs:documentation (PT-BR) — a matéria-prima das 3 camadas do catálogo:
//
//   1. ordem  : o "# XML" legado segue a ordem do documento (âncoras calibram);
//   2. semântica: a Descrição da planilha casa com a xs:documentation;
//   3. valor  : o XPath ancorado no gabarito precisa existir aqui (senão é
//               extensão não-SEFAZ, ex.: dadosAdic).
//
// A caminhada parte de TEnviNFe (raiz sintética <enviNFe>) e entra em TNFe.
// Atributos são emitidos LOGO APÓS o elemento dono (no leiaute oficial,
// versao/Id vêm imediatamente depois de infNFe — calibração: NFe=1, infNFe=2,
// versao=3, Id=4, ide=5, cUF=6 ✓). ds:Signature (ref externo) fica de fora.
//
// Desenho: docs/architecture/poc-excel-generator.md §3.4.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Um nó do leiaute NF-e, em ordem de documento do XSD.</summary>
/// <param name="Order">Índice sequencial global na caminhada (ordem do documento).</param>
/// <param name="XPath">Caminho sem namespace: "enviNFe/NFe/infNFe/ide/cUF" (atributo: ".../@versao").</param>
/// <param name="Name">Nome local ("cUF", "versao").</param>
/// <param name="IsAttribute">É um xs:attribute (versao, Id, nItem…).</param>
/// <param name="IsGroup">Tem filhos (elemento estrutural: ide, emit, det…).</param>
/// <param name="TypeName">Tipo declarado ou base da restrição inline ("TCodUfIBGE", "xs:string").</param>
/// <param name="Occurs">"1-1", "0-1", "1-990", "0-N"…</param>
/// <param name="Documentation">xs:documentation (whitespace normalizado) ou vazio.</param>
/// <param name="InChoice">Está dentro de um xs:choice (variante, ex.: ICMS00…ICMS90).</param>
public sealed record XsdLeiauteNode(
    int Order,
    string XPath,
    string Name,
    bool IsAttribute,
    bool IsGroup,
    string TypeName,
    string Occurs,
    string Documentation,
    bool InChoice);

/// <summary>
/// Índice em ordem de documento do leiaute NF-e (enviNFe + NFe/infNFe).
/// Carregamento é 100% determinístico — sem LLM, sem rede.
/// </summary>
public sealed class XsdLeiauteIndex
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, XsdLeiauteNode> _byPath;
    private readonly ILookup<string, XsdLeiauteNode> _byName;

    /// <summary>Todos os nós, em ordem de documento (Order crescente).</summary>
    public IReadOnlyList<XsdLeiauteNode> Nodes { get; }

    private XsdLeiauteIndex(List<XsdLeiauteNode> nodes)
    {
        Nodes = nodes;
        _byPath = nodes.ToDictionary(n => n.XPath, StringComparer.Ordinal);
        _byName = nodes.ToLookup(n => n.Name, StringComparer.Ordinal);
    }

    /// <summary>Carrega e caminha o XSD (includes de tipos simples não são necessários).</summary>
    public static XsdLeiauteIndex Load(string xsdPath)
    {
        if (!File.Exists(xsdPath))
            throw new FileNotFoundException($"XSD do leiaute não encontrado: {xsdPath}", xsdPath);

        var schema = XDocument.Load(xsdPath).Root
            ?? throw new InvalidOperationException("XSD sem elemento raiz.");

        // Tipos complexos nomeados do próprio leiaute (TNFe, TEnderEmi, TIpi…).
        var named = schema.Elements(Xs + "complexType")
            .Where(t => t.Attribute("name") is not null)
            .ToDictionary(t => t.Attribute("name")!.Value, t => t, StringComparer.Ordinal);

        if (!named.TryGetValue("TEnviNFe", out var envi))
            throw new InvalidOperationException("Tipo TEnviNFe não encontrado no XSD (leiaute errado?).");

        var nodes = new List<XsdLeiauteNode>(2048);
        // Alguns elementos repetem o MESMO XPath em ramos distintos de um choice
        // (ex.: IPI nos dois ramos de imposto) — a 1ª ocorrência vence.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Add(nodes, seen, "enviNFe", "enviNFe", isAttribute: false, isGroup: true,
            type: "TEnviNFe", occurs: "1-1", doc: DocOf(envi), inChoice: false);
        WalkComplexType(envi, "enviNFe", named, nodes, seen, inChoice: false, depth: 0);
        return new XsdLeiauteIndex(nodes);
    }

    /// <summary>Nó exato por XPath (sem namespace, sem "/" inicial).</summary>
    public bool TryByPath(string xpath, out XsdLeiauteNode node)
    {
        if (_byPath.TryGetValue(xpath.TrimStart('/'), out var n)) { node = n; return true; }
        node = default!;
        return false;
    }

    /// <summary>Todos os nós com um dado nome local (ex.: "CST" aparece em cada variante).</summary>
    public IReadOnlyList<XsdLeiauteNode> ByName(string name) => _byName[name].ToList();

    /// <summary>Nós cujo XPath começa com o prefixo (subárvore, em ordem).</summary>
    public IReadOnlyList<XsdLeiauteNode> Subtree(string pathPrefix) =>
        Nodes.Where(n => n.XPath.StartsWith(pathPrefix, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Primeiro nó de grupo cujo XPath termina com o sufixo dado
    /// (ex.: "infNFe/ide" → nó de ide). Null se não achar.
    /// </summary>
    public XsdLeiauteNode? GroupBySuffix(string suffix) =>
        Nodes.FirstOrDefault(n => n.IsGroup && n.XPath.EndsWith(suffix, StringComparison.Ordinal));

    // ── Caminhada (ordem do documento) ────────────────────────────────────────

    private static void WalkComplexType(
        XElement ct, string path, Dictionary<string, XElement> named,
        List<XsdLeiauteNode> nodes, HashSet<string> seen, bool inChoice, int depth)
    {
        if (depth > 64) return;   // guarda-corpo (o leiaute real tem ~12 níveis)

        // Atributos do tipo (diretos + via simpleContent/extension).
        foreach (var at in AttributesOf(ct))
        {
            var an = at.Attribute("name")?.Value;
            if (an is null) continue;
            var occurs = at.Attribute("use")?.Value == "required" ? "1-1" : "0-1";
            Add(nodes, seen, $"{path}/@{an}", an, isAttribute: true, isGroup: false,
                type: at.Attribute("type")?.Value ?? "", occurs: occurs, doc: DocOf(at), inChoice: inChoice);
        }

        foreach (var child in ct.Elements())
            WalkParticle(child, path, named, nodes, seen, inChoice, depth);
    }

    private static void WalkParticle(
        XElement particle, string path, Dictionary<string, XElement> named,
        List<XsdLeiauteNode> nodes, HashSet<string> seen, bool inChoice, int depth)
    {
        if (particle.Name == Xs + "sequence" || particle.Name == Xs + "all")
        {
            foreach (var child in particle.Elements())
                WalkParticle(child, path, named, nodes, seen, inChoice, depth);
        }
        else if (particle.Name == Xs + "choice")
        {
            // Variantes (ICMS00…ICMS90, CNPJ|CPF…): todas entram no índice.
            foreach (var child in particle.Elements())
                WalkParticle(child, path, named, nodes, seen, inChoice: true, depth: depth);
        }
        else if (particle.Name == Xs + "element")
        {
            WalkElement(particle, path, named, nodes, seen, inChoice, depth);
        }
        // annotation/attribute etc. já tratados ou irrelevantes aqui.
    }

    private static void WalkElement(
        XElement el, string parentPath, Dictionary<string, XElement> named,
        List<XsdLeiauteNode> nodes, HashSet<string> seen, bool inChoice, int depth)
    {
        // Único ref no leiaute é ds:Signature — fora do domínio do catálogo.
        if (el.Attribute("ref") is not null) return;

        var name = el.Attribute("name")?.Value;
        if (name is null) return;

        var path = $"{parentPath}/{name}";
        var typeName = el.Attribute("type")?.Value ?? "";
        var doc = DocOf(el);

        // Tipo complexo: inline ou nomeado (TNFe, TEnderEmi, TLocal, TIpi…).
        var ct = el.Element(Xs + "complexType");
        if (ct is null && typeName.Length > 0 && named.TryGetValue(typeName, out var nt))
        {
            ct = nt;
            if (doc.Length == 0) doc = DocOf(nt);
        }

        // Tipo simples inline: registra a base da restrição como tipo.
        if (typeName.Length == 0 && el.Element(Xs + "simpleType") is { } st)
            typeName = st.Descendants(Xs + "restriction").FirstOrDefault()?.Attribute("base")?.Value ?? "";

        var isGroup = ct is not null && ct.Descendants(Xs + "element").Any();
        // XPath repetido (mesmo elemento em ramos distintos do choice) → 1ª vence.
        if (!Add(nodes, seen, path, name, isAttribute: false, isGroup: isGroup,
                type: typeName, occurs: OccursOf(el), doc: doc, inChoice: inChoice))
            return;

        if (ct is not null)
            WalkComplexType(ct, path, named, nodes, seen, inChoice, depth + 1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<XElement> AttributesOf(XElement ct)
    {
        foreach (var at in ct.Elements(Xs + "attribute")) yield return at;
        var ext = ct.Element(Xs + "simpleContent")?.Element(Xs + "extension")
               ?? ct.Element(Xs + "complexContent")?.Element(Xs + "extension");
        if (ext is not null)
            foreach (var at in ext.Elements(Xs + "attribute")) yield return at;
    }

    private static string OccursOf(XElement el)
    {
        var min = el.Attribute("minOccurs")?.Value ?? "1";
        var max = el.Attribute("maxOccurs")?.Value ?? "1";
        if (max == "unbounded") max = "N";
        return $"{min}-{max}";
    }

    private static string DocOf(XElement el)
    {
        var doc = el.Element(Xs + "annotation")?.Element(Xs + "documentation")?.Value;
        return doc is null ? "" : Ws.Replace(doc, " ").Trim();
    }

    private static bool Add(
        List<XsdLeiauteNode> nodes, HashSet<string> seen, string path, string name,
        bool isAttribute, bool isGroup, string type, string occurs, string doc, bool inChoice)
    {
        if (!seen.Add(path)) return false;
        nodes.Add(new XsdLeiauteNode(
            nodes.Count, path, name, isAttribute, isGroup, type, occurs, doc, inChoice));
        return true;
    }
}
