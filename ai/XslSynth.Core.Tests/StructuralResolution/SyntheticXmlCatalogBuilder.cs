using XslSynth.Core.StructuralResolution;
using XslSynth.Model;

namespace XslSynth.Core.Tests.StructuralResolution;

/// <summary>
/// Constrói uma árvore <see cref="XmlLayoutNode"/> sintética em memória (sem XSD/arquivo) para
/// cobrir os casos de composição/ocorrência da issue #140 (design §6.1) que não dependem da
/// estrutura real da NF-e — mais direto que fabricar XSD para cada combinação de dimensão.
///
/// Árvore:
/// Doc (urn:test:synthetic)
///  ├─ Cabecalho
///  │   ├─ CampoA
///  │   └─ CampoB
///  ├─ Itens
///  │   └─ Item (maxOccurs=3)
///  │       ├─ @Seq (atributo)
///  │       ├─ Valor
///  │       └─ SubItens
///  │           └─ SubItem (maxOccurs=unbounded)      ← grupo repetido aninhado (2 níveis)
///  │               └─ SubValor
///  ├─ Total
///  └─ Chave
/// </summary>
internal static class SyntheticXmlCatalogBuilder
{
    private const string Ns = "urn:test:synthetic";

    public static XmlLayoutCatalog Build()
    {
        var subValor = Element("SubValor", "Doc/Itens/Item/SubItens/SubItem");
        var subItem = Element("SubItem", "Doc/Itens/Item/SubItens", maxOccurs: null, children: new() { subValor });

        var subItens = Element("SubItens", "Doc/Itens/Item", children: new() { subItem });
        var itemSeq = Attribute("Seq", "Doc/Itens/Item");
        var valor = Element("Valor", "Doc/Itens/Item");
        var item = Element("Item", "Doc/Itens", maxOccurs: 3, children: new() { itemSeq, valor, subItens });

        var itens = Element("Itens", "Doc", children: new() { item });
        var campoA = Element("CampoA", "Doc/Cabecalho");
        var campoB = Element("CampoB", "Doc/Cabecalho");
        var cabecalho = Element("Cabecalho", "Doc", children: new() { campoA, campoB });
        var total = Element("Total", "Doc");
        var chave = Element("Chave", "Doc");

        var root = Element("Doc", parentPath: null, children: new() { cabecalho, itens, total, chave });

        return new XmlLayoutCatalog(root);
    }

    private static XmlLayoutNode Element(string name, string? parentPath, int? maxOccurs = 1, List<XmlLayoutNode>? children = null)
    {
        var path = parentPath is null ? name : $"{parentPath}/{name}";
        var node = new XmlLayoutNode
        {
            NodePath = path,
            Kind = XmlNodeKind.Element,
            Name = name,
            Namespace = Ns,
            ParentPath = parentPath,
            MinOccurs = 1,
            MaxOccurs = maxOccurs
        };
        if (children is not null)
        {
            node.Children.AddRange(children);
        }
        return node;
    }

    private static XmlLayoutNode Attribute(string name, string parentPath) => new()
    {
        NodePath = $"{parentPath}/@{name}",
        Kind = XmlNodeKind.Attribute,
        Name = name,
        Namespace = null,
        ParentPath = parentPath,
        MinOccurs = 0,
        MaxOccurs = 1
    };
}
