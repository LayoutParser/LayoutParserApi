using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XslSynth.NtPipeline;

// ─────────────────────────────────────────────────────────────────────────────
// Modelos do S1 (XsdDiff) do pipeline "NT nova": o delta entre dois pacotes XSD
// é POR XPATH, tipado e serializável — o artefato versionável que alimenta os
// estágios S2 (semântica via PDF da NT) e S3 (delta do catálogo #XML→XPath).
// Desenho: docs/architecture/nt-pipeline-design.md §4–5 (P-1).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Classificação de uma divergência entre o XSD velho e o novo.</summary>
public enum XsdDeltaKind
{
    /// <summary>Nó existe só no XSD novo.</summary>
    Added,

    /// <summary>Nó existe só no XSD velho.</summary>
    Removed,

    /// <summary>Mesmo XPath, tipo declarado diferente.</summary>
    TypeChanged,

    /// <summary>Mesmo XPath, ocorrência (minOccurs/maxOccurs ou use) diferente.</summary>
    OccurrenceChanged,

    /// <summary>Mesmo XPath e mesmo tipo, facets (tamanho/pattern/enum…) diferentes.</summary>
    FacetChanged,
}

/// <summary>Categoria de um nó no snapshot "diffável" do XSD.</summary>
public enum XsdNodeKind
{
    Elemento,
    Atributo,
    TipoComplexo,
    TipoSimples,
}

/// <summary>Um nó do snapshot do pacote XSD. O XPath é a CHAVE do diff.</summary>
/// <param name="Order">Índice sequencial na caminhada (ordem de documento).</param>
/// <param name="XPath">Caminho enraizado no tipo/elemento global ("TNFe/infNFe/ide/cUF"; atributo: ".../@versao").</param>
/// <param name="Kind">Categoria do nó.</param>
/// <param name="TypeName">Tipo declarado ("TCodUfIBGE", "xs:string", "(simples inline: TString)").</param>
/// <param name="Occurs">"1-1", "0-N"… ("global" para declarações de topo).</param>
/// <param name="Facets">Assinatura de facets (base + tamanho/pattern/enum…) ou vazio.</param>
public sealed record XsdDiffNode(
    int Order,
    string XPath,
    XsdNodeKind Kind,
    string TypeName,
    string Occurs,
    string Facets);

/// <summary>Uma divergência classificada. Antes/Depois carregam SÓ a parte que mudou.</summary>
public sealed record XsdDeltaEntry(
    XsdDeltaKind Kind,
    string XPath,
    string? Antes,
    string? Depois);

/// <summary>Artefato do S1: o delta completo entre dois XSD (JSON versionável).</summary>
public sealed class XsdDelta
{
    public required string XsdVelho { get; init; }
    public required string XsdNovo { get; init; }
    public DateTime GeradoEmUtc { get; init; } = DateTime.UtcNow;
    public int NosVelho { get; init; }
    public int NosNovo { get; init; }

    /// <summary>Contagem por tipo de delta (sempre as 5 chaves — schema estável p/ S2/S3).</summary>
    public required Dictionary<string, int> Resumo { get; init; }

    public required List<XsdDeltaEntry> Entradas { get; init; }

    [JsonIgnore]
    public int Total => Entradas.Count;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // Padrão do projeto: preservar '<'/'>' e regex de pattern legíveis no JSON.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
}
