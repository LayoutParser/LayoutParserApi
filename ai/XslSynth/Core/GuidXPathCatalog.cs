using System.Xml.Linq;

namespace XslSynth.Core;

// ─────────────────────────────────────────────────────────────────────────────
// GuidXPathCatalog — A3 (Trilha A, plano multi-sessão §7.4): resolve os GUIDs
// reais do Sysmiddle (TAG_/GRT_/ATT_ do TargetLayoutGuid; LIN_/FLD_ do
// InputLayoutGuid) para o XPath COMPLETO no leiaute NF-e, destravando os 237
// LinkMappings do MapperVO real (hoje resolvidos só até a FOLHA, por convenção
// de nome — ver LinkMappingTranspiler §"LIMITE HONESTO").
//
// Fonte: o arquivo LayoutVO exportado do Connect Us (ex.:
// Documentos/Layout/layout-nfe.xml) — a MESMA árvore que o painel usa para
// desenhar o leiaute, com <ElementGuid> em cada nó. Achado no ambiente local:
// o LayoutGuid desse arquivo (LAY_767be1dd-…) bate EXATAMENTE com o
// TargetLayoutGuid do mapeador SEND_ENV real — não é fixture, é o leiaute de
// produção. Estrutura (por xsi:type):
//   GroupTagElementVO / TagElementVO → contribuem um segmento de XPath (Name).
//   AttributeElementVO               → contribui "@Name" no XPath do PAI.
//   ChoiceElementVO / SequenceElementVO → wrappers ESTRUTURAIS puramente de
//     ocorrência (Name literal "Choice"/"Sequence") — NÃO entram no XPath,
//     só repassam o caminho do pai aos filhos (mesma convenção do XSD
//     xs:choice/xs:sequence em XsdLeiauteIndex).
//
// 100% determinístico, sem LLM. Degrade gracioso: arquivo ausente/formato
// inesperado → catálogo vazio (Resolve retorna false), nunca derruba o
// chamador (ver .claude/rules/dotnet-standards.md — resiliência).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Um nó do LayoutVO real, indexado por <see cref="ElementGuid"/>.</summary>
/// <param name="ElementGuid">GUID prefixado (TAG_/GRT_/ATT_/FLD_/LIN_…) — chave do catálogo.</param>
/// <param name="XPath">Caminho sem namespace ("enviNFe/NFe/infNFe/dest/enderDest/xMun"; atributo: ".../@versao").</param>
/// <param name="Name">Nome local do elemento/atributo.</param>
/// <param name="IsAttribute">Veio de um AttributeElementVO.</param>
/// <param name="IsGroup">Tem filhos próprios no LayoutVO (GroupTagElementVO com Elements).</param>
public sealed record GuidXPathEntry(string ElementGuid, string XPath, string Name, bool IsAttribute, bool IsGroup);

/// <summary>Catálogo GUID→XPath construído a partir de um LayoutVO exportado (Connect Us).</summary>
public sealed class GuidXPathCatalog
{
    // Wrappers estruturais do LayoutVO: Name literal, NÃO viram segmento de XPath.
    private static readonly HashSet<string> WrapperTypes = new(StringComparer.Ordinal)
        { "ChoiceElementVO", "SequenceElementVO" };

    private readonly Dictionary<string, GuidXPathEntry> _byGuid;

    /// <summary>Todos os nós resolvidos (para diagnóstico/relatório).</summary>
    public IReadOnlyCollection<GuidXPathEntry> Entries => _byGuid.Values;

    /// <summary>GUID do LayoutVO (LayoutGuid) — para conferir contra InputLayoutGuid/TargetLayoutGuid do mapeador.</summary>
    public string? LayoutGuid { get; }

    private GuidXPathCatalog(Dictionary<string, GuidXPathEntry> byGuid, string? layoutGuid)
    {
        _byGuid = byGuid;
        LayoutGuid = layoutGuid;
    }

    /// <summary>
    /// Carrega um LayoutVO (XML exportado do Connect Us, xsi:type="XmlLayoutVO"/"TextLayoutVO").
    /// Degrade gracioso: arquivo ausente ou raiz inesperada → catálogo VAZIO (nunca lança).
    /// </summary>
    public static GuidXPathCatalog Load(string layoutPath, Action<string>? log = null)
    {
        if (!File.Exists(layoutPath))
        {
            log?.Invoke($"   [aviso] LayoutVO não encontrado ({layoutPath}) — catálogo GUID→XPath vazio.");
            return new GuidXPathCatalog(new Dictionary<string, GuidXPathEntry>(StringComparer.Ordinal), null);
        }

        XElement root;
        try
        {
            // Mesma pegadinha do MapperVO (RealMapperParser): o LayoutVO exportado
            // declara encoding="utf-16" no prólogo, mas os bytes são UTF-8 (com
            // BOM) — XDocument.Load direto falha ("no Unicode byte order mark").
            var text = RealMapperParser.DecodeAndFixDeclaration(File.ReadAllBytes(layoutPath));
            root = XDocument.Parse(text).Root
                ?? throw new InvalidOperationException("LayoutVO sem elemento raiz.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"   [aviso] LayoutVO ilegível ({ex.Message}) — catálogo GUID→XPath vazio.");
            return new GuidXPathCatalog(new Dictionary<string, GuidXPathEntry>(StringComparer.Ordinal), null);
        }

        var layoutGuid = (string?)root.Element("LayoutGuid");
        var byGuid = new Dictionary<string, GuidXPathEntry>(StringComparer.Ordinal);

        var elementsRoot = root.Element("Elements");
        if (elementsRoot is not null)
            foreach (var el in elementsRoot.Elements("Element"))
                Caminha(el, basePath: "", byGuid);

        log?.Invoke($"   [guid-catalog] {byGuid.Count} GUIDs resolvidos de '{Path.GetFileName(layoutPath)}' "
            + $"(LayoutGuid={layoutGuid ?? "?"}).");
        return new GuidXPathCatalog(byGuid, layoutGuid);
    }

    private static void Caminha(XElement el, string basePath, Dictionary<string, GuidXPathEntry> byGuid)
    {
        // §trim: o LayoutVO exportado vem "pretty-printed" com quebras de linha
        // DENTRO do texto de <ElementGuid>/<Name> (indentação profunda passa de
        // 120+ colunas) — sem o Trim(), a chave do dicionário carrega espaços/
        // newlines embutidos e nunca casa com o GUID limpo do MapperVO (achado
        // real: só 1/237 LinkMappings resolvia antes deste fix).
        var tipo = ((string?)el.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type"))?.Trim() ?? "";
        var guid = ((string?)el.Element("ElementGuid"))?.Trim();
        var name = ((string?)el.Element("Name"))?.Trim() ?? "";
        var filhos = el.Element("Elements");

        var ehAtributo = tipo == "AttributeElementVO";
        var ehWrapper = WrapperTypes.Contains(tipo);

        // Wrapper (Choice/Sequence): NÃO adiciona segmento — repassa o path do pai aos filhos.
        var path = ehWrapper ? basePath
            : ehAtributo ? $"{basePath}/@{name}"
            : string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

        if (guid is not null && !ehWrapper)
        {
            byGuid[guid] = new GuidXPathEntry(guid, path, name, ehAtributo, filhos is not null);
        }

        if (filhos is not null)
        {
            // Atributo dono do caminho para os filhos: o do NÓ atual (sem alterar
            // para atributo — atributo nunca tem filhos no LayoutVO na prática).
            var pathParaFilhos = ehWrapper ? basePath : path;
            foreach (var filho in filhos.Elements("Element"))
                Caminha(filho, pathParaFilhos, byGuid);
        }
    }

    /// <summary>Resolve um GUID (TAG_/GRT_/ATT_/FLD_/LIN_…) para o XPath completo. Degrade: não encontrado → false.</summary>
    public bool TryResolve(string? guid, out GuidXPathEntry entry)
    {
        entry = null!;
        return guid is not null && _byGuid.TryGetValue(guid, out entry!);
    }

    public int Count => _byGuid.Count;
}
