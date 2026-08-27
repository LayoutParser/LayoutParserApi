using XslSynth.Model;

namespace XslSynth.Core.StructuralResolution;

/// <summary>
/// Índice sobre a árvore produzida por <see cref="XmlLayoutStructureParser"/> (design §2.2-2.3):
/// resolve um caminho de nomes ponto-a-ponto (ex.: <c>"NFe/infNFe/det/prod/CFOP"</c> — já é a
/// forma que <c>MapperRule.TargetPath</c> carrega, derivado da primeira atribuição <c>T.</c> da
/// DSL) para o nó da árvore, e constrói o XPath absoluto com namespace.
///
/// Cache: mantido pelo chamador (mesmo padrão de <c>MapperCacheService</c>/<c>CachedLayoutService</c>
/// — este catálogo em si é só um índice imutável sobre uma árvore já construída, não faz I/O).
/// </summary>
public sealed class XmlLayoutCatalog
{
    private readonly Dictionary<string, XmlLayoutNode> _byPath;
    private readonly ILookup<string, XmlLayoutNode> _byLeafName;
    private readonly Dictionary<string, string> _prefixByNamespace = new(StringComparer.Ordinal);

    public XmlLayoutNode Root { get; }

    public XmlLayoutCatalog(XmlLayoutNode root)
    {
        Root = root;
        var all = Flatten(root).ToList();
        // xs:choice pode ter mais de uma alternativa com o MESMO nome de elemento em pontos
        // distintos da definição (ex.: "IPI" aparece em ramos de choice diferentes dentro de
        // imposto/det na NF-e real) — mesmo caminho de nomes resultante. Mantém o primeiro nó
        // encontrado; semanticamente é o mesmo ponto navegável na árvore, não uma colisão real.
        _byPath = all
            .GroupBy(n => n.NodePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _byLeafName = all
            .GroupBy(n => n.NodePath, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToLookup(n => n.Name, StringComparer.Ordinal);

        // Convenção do design (§2.2): NF-e usa um único namespace default — prefixo fixo "nfe"
        // só para ele. Qualquer outro namespace (ex.: schema sintético de teste, ou um domínio XML
        // futuro diferente de NF-e) recebe prefixo gerado sob demanda em RegisterPrefix.
        if (root.Namespace == "http://www.portalfiscal.inf.br/nfe")
        {
            _prefixByNamespace[root.Namespace] = "nfe";
        }
    }

    private static IEnumerable<XmlLayoutNode> Flatten(XmlLayoutNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Resolução por caminho de nomes exato (sem ambiguidade — é o caso "authoritative").</summary>
    public XmlLayoutNode? TryResolveByPath(string dottedNamePath) =>
        _byPath.TryGetValue(dottedNamePath, out var node) ? node : null;

    /// <summary>Resolução por nome de folha — pode ser ambígua (múltiplos nós com o mesmo nome em
    /// pontos diferentes da árvore, ex.: "CFOP" só existe uma vez na NF-e mas "vProd" aparece em
    /// vários níveis). Usado só como fallback best-effort quando não há caminho completo
    /// disponível (ex.: <c>LinkMappingItem.TargetLeafName</c>).</summary>
    public IReadOnlyList<XmlLayoutNode> ResolveByLeafName(string leafName) =>
        _byLeafName[leafName].ToList();

    /// <summary>Ancestrais do nó (do pai imediato até a raiz), usado pela heurística de ocorrência (design §4.2).</summary>
    public IEnumerable<XmlLayoutNode> Ancestors(XmlLayoutNode node)
    {
        var current = node.ParentPath is null ? null : TryResolveByPath(node.ParentPath);
        while (current is not null)
        {
            yield return current;
            current = current.ParentPath is null ? null : TryResolveByPath(current.ParentPath);
        }
    }

    /// <summary>XPath absoluto com namespace (design §2.2). Atributo vira <c>@Name</c> no último
    /// segmento; nó de texto aponta ao elemento pai (o <c>NodeKind</c> no <c>XmlNodeReference</c>
    /// já sinaliza "texto" sem precisar de <c>/text()</c> no XPath).</summary>
    public string BuildAbsoluteXPath(XmlLayoutNode node)
    {
        var chain = new List<XmlLayoutNode> { node };
        var current = node.ParentPath is null ? null : TryResolveByPath(node.ParentPath);
        while (current is not null)
        {
            chain.Add(current);
            current = current.ParentPath is null ? null : TryResolveByPath(current.ParentPath);
        }
        chain.Reverse();

        var segments = chain.Select(n =>
        {
            var qualifiedName = n.Namespace is null ? n.Name : $"{Prefix(n.Namespace)}:{n.Name}";
            return n.Kind == XmlNodeKind.Attribute ? $"@{qualifiedName}" : qualifiedName;
        });

        return "/" + string.Join("/", segments);
    }

    private string Prefix(string ns) =>
        _prefixByNamespace.TryGetValue(ns, out var prefix) ? prefix : RegisterPrefix(ns);

    private string RegisterPrefix(string ns)
    {
        // Namespace novo (não-default) — a NF-e usa só um namespace na prática (design §2.2),
        // mas o modelo não assume isso permanentemente: gera prefixo determinístico "nsN".
        var prefix = $"ns{_prefixByNamespace.Count}";
        _prefixByNamespace[ns] = prefix;
        return prefix;
    }
}
