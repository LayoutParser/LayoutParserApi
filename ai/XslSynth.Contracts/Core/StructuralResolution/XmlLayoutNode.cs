using XslSynth.Model;

namespace XslSynth.Core.StructuralResolution;

/// <summary>
/// Nó da árvore de estrutura XML de destino (design §2.1). Diferente do <c>LayoutVO</c> exportado
/// do Sysmiddle (que teria GUIDs <c>TAG_/ATT_/GRT_</c> reais), este nó vem do XSD SEFAZ — fonte
/// única de verdade decidida pelo dono para a issue #140 (NF-e por ora, extensível por tipo de
/// documento). O "ElementGuid" aqui é sintético e determinístico (o próprio caminho de nomes),
/// não um GUID Sysmiddle — não confundir com <c>MapperRule.TargetElementGuid</c>.
/// </summary>
public sealed class XmlLayoutNode
{
    /// <summary>Identificador sintético e determinístico: caminho de nomes desde a raiz (ex.: "NFe/infNFe/det/prod/CFOP").</summary>
    public required string NodePath { get; init; }

    public required XmlNodeKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>URI do namespace do elemento/atributo — null quando sem namespace (atributos unqualified, comum na NF-e).</summary>
    public string? Namespace { get; init; }

    public string? ParentPath { get; init; }

    public List<XmlLayoutNode> Children { get; } = new();

    public int MinOccurs { get; init; } = 1;

    /// <summary>Null representa "ilimitado" (<c>xs:unbounded</c>).</summary>
    public int? MaxOccurs { get; init; } = 1;

    /// <summary>Repetição real no XSD — usado pela heurística de ocorrência (design §4.2).</summary>
    public bool IsRepeatable => MaxOccurs is null or > 1;
}
