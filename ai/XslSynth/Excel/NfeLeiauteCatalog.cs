using System.Text.RegularExpressions;

namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// NfeLeiauteCatalog — PoC-2: resolvedor "# XML" (nº do leiaute na spec Excel)
// → XPath NF-e, por TRIANGULAÇÃO de 3 sinais determinísticos (SEM LLM):
//
//   1. XsdOrder    — a numeração legada segue a ordem do documento; âncoras
//                    empíricas (arquiteto) + pins calibram; interpolação SÓ em
//                    janelas exatas (nº de nós == nº de números) — a numeração
//                    tem buracos não deriváveis (17-24, 9=indPag removido).
//   2. Semantic    — Descrição (col C) × xs:documentation PT-BR do XSD
//                    (Dice ponderado por IDF, janela restrita quando possível).
//   3. ValueAnchor — valor fatiado do gabarito real (TXT) encontrado em
//                    exatamente UM XPath do XML de saída de produção.
//
// Confiança: Alta = 2+ sinais concordam · Média = 1 sinal · Baixa = conflito
// (prioridade ValueAnchor > Semantic > XsdOrder) · NaoResolvido = 0 sinais.
//
// Esquemas de ref na spec real (levantados empiricamente):
//   • "6".."408"      numeração sequencial legada (pré-4.00; 9=indPag morto);
//   • "29a","398a2"   inseridos por NT (sufixo letra); "29x.4" = grupo NFref;
//   • "104.08","324.107" subrefs por NT ("324107" = "324.107" sem o ponto;
//                     família 324.x = IBS/CBS/IS da NT2025.002);
//   • "B25c","YA04"   esquema MOC por grupo (B=ide, N=ICMS, YA=pag, R=PISST…);
//   • "166\n173\n…"   multi-ref: um campo TXT alimenta a MESMA folha em várias
//                     variantes do choice ICMS → XPath curinga (ICMS/*/pICMS);
//   • "FIAT","ANFAVEA" extensões proprietárias (dadosAdic) — fora do XSD.
//
// Desenho: docs/architecture/poc-excel-generator.md §3.4.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Entrada resolvida do catálogo (contrato do design §3.2).</summary>
public sealed record LeiauteEntry(string XmlRef, string XPath, string Tipo, string Occurs);

/// <summary>Resolve um "# XML" da spec para o destino no leiaute NF-e.</summary>
public interface INfeLeiauteCatalog
{
    bool TryResolve(string xmlRef, out LeiauteEntry e);
}

/// <summary>Sinal(is) que resolveu(ram) uma entrada — para o relatório honesto.</summary>
[Flags]
public enum SinalResolucao
{
    Nenhum = 0,
    XsdOrder = 1,
    Semantic = 2,
    ValueAnchor = 4
}

/// <summary>Nível de confiança da resolução.</summary>
public enum NivelConfianca { NaoResolvido, Baixa, Media, Alta }

/// <summary>Categoria sintática do ref (para agrupar cobertura e gaps).</summary>
public enum RefCategoria { PuroNumerico, LetraSufixo, SubRef, MocId, MultiRef, Extensao, Outro }

/// <summary>Diagnóstico completo de um ref distinto (alimenta o relatório).</summary>
/// <param name="XmlRef">Chave normalizada ("324.107"; multi-ref = partes unidas por "|").</param>
/// <param name="Categoria">Classe sintática do ref.</param>
/// <param name="XPath">Destino resolvido (null = não resolvido). Multi-ref pode ter curinga "/*/".</param>
/// <param name="Tipo">Tipo XSD do nó destino ("" quando fora do XSD).</param>
/// <param name="Occurs">Ocorrência do nó ("choice" para curinga de variantes).</param>
/// <param name="Sinais">Quais camadas sustentam a resolução.</param>
/// <param name="Confianca">Alta/Média/Baixa/NaoResolvido.</param>
/// <param name="ForaDoXsd">Resolvido para extensão não-SEFAZ (dadosAdic).</param>
/// <param name="CamposNaSpec">Quantos campos da planilha usam este ref.</param>
/// <param name="Descricao">1ª descrição da spec (para o relatório).</param>
/// <param name="Observacao">Conflitos/ambiguidades registrados honestamente.</param>
public sealed record CatalogResolution(
    string XmlRef,
    RefCategoria Categoria,
    string? XPath,
    string Tipo,
    string Occurs,
    SinalResolucao Sinais,
    NivelConfianca Confianca,
    bool ForaDoXsd,
    int CamposNaSpec,
    string Descricao,
    string? Observacao);

/// <summary>
/// Catálogo #XML → XPath NF-e, construído por triangulação determinística.
/// </summary>
public sealed class NfeLeiauteCatalog : INfeLeiauteCatalog
{
    // Âncoras EMPÍRICAS confirmadas pelo arquiteto no par gabarito real
    // (LINHA001[10-11]='31'→cUF etc.). Calibram a camada de ordem.
    private static readonly (int N, string Sufixo)[] AncorasArquiteto =
    [
        (6, "ide/cUF"), (8, "ide/natOp"), (10, "ide/mod"), (11, "ide/serie"),
        (12, "ide/nNF"), (13, "ide/dhEmi"), (14, "ide/dhSaiEnt"), (15, "ide/tpNF"),
        (16, "ide/cMunFG")
    ];

    // Esquema MOC (letra do grupo → sufixo do grupo no XSD). Prefixo mais longo vence.
    private static readonly (string Prefixo, string GrupoSufixo)[] GruposMoc =
    [
        ("YA", "infNFe/pag"), ("YB", "infNFe/pag"), ("BB", "NFe/infNFe"),
        ("B", "infNFe/ide"), ("C", "infNFe/emit"), ("E", "infNFe/dest"),
        ("F", "infNFe/retirada"), ("G", "infNFe/entrega"), ("I", "det/prod"),
        ("M", "det/imposto"), ("N", "imposto/ICMS"), ("O", "imposto/IPI"),
        ("P", "imposto/II"), ("Q", "imposto/PIS"), ("R", "imposto/PISST"),
        ("S", "imposto/COFINS"), ("T", "imposto/COFINSST"), ("U", "imposto/ISSQN"),
        ("W", "infNFe/total"), ("X", "infNFe/transp"), ("Y", "infNFe/cobr"),
        ("Z", "infNFe/infAdic")
    ];

    /// <summary>Janela máxima (em números) para interpolação por ordem.</summary>
    private const int MaxJanelaInterp = 25;

    /// <summary>Janela máxima (em nós) para busca semântica de refs sufixados.</summary>
    private const int MaxJanelaSufixo = 600;

    private readonly Dictionary<string, LeiauteEntry> _resolved;
    private readonly Dictionary<string, string> _aliasParaChave;

    /// <summary>Diagnóstico de todos os refs distintos (ordenado por categoria/chave).</summary>
    public IReadOnlyList<CatalogResolution> Resolutions { get; }

    private NfeLeiauteCatalog(
        List<CatalogResolution> resolutions,
        Dictionary<string, LeiauteEntry> resolved,
        Dictionary<string, string> alias)
    {
        Resolutions = resolutions;
        _resolved = resolved;
        _aliasParaChave = alias;
    }

    /// <inheritdoc/>
    public bool TryResolve(string xmlRef, out LeiauteEntry e)
    {
        e = default!;
        if (string.IsNullOrWhiteSpace(xmlRef)) return false;
        var key = _aliasParaChave.TryGetValue(xmlRef.Trim(), out var k) ? k : xmlRef.Trim();
        return _resolved.TryGetValue(key, out e!);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Construção (pipeline de triangulação)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Constrói o catálogo. O gabarito é OPCIONAL: sem ele o catálogo degrada
    /// graciosamente para 2 camadas (ordem + semântica) — nunca lança por isso.
    /// <paramref name="debugRef"/> liga o diagnóstico detalhado de um ref (dev).
    /// </summary>
    public static NfeLeiauteCatalog Build(
        SpecModel spec, XsdLeiauteIndex xsd, NfeGabarito? gabarito, Action<string>? log = null,
        string? debugRef = null)
    {
        // ── 0. Agrupa campos da spec por ref normalizado ─────────────────────
        var contextos = BlockContexts(spec);
        var works = CollectRefs(spec, contextos);
        var idf = SemanticMatcher.BuildIdf(xsd.Nodes);
        var leafNodes = xsd.Nodes.Where(n => !n.IsGroup).ToList();

        // ── 1. Camada 3: ancoragem por VALOR no gabarito ─────────────────────
        if (gabarito is not null)
        {
            foreach (var w in works.Values.Where(w => w.Categoria != RefCategoria.MultiRef))
            {
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in w.Fields)
                    paths.UnionWith(gabarito.FindAnchorPaths(f));
                w.ValuePaths = paths.ToList();
            }
        }

        // ── 2. Âncoras do arquiteto (calibração empírica da ordem) ───────────
        var anchorNodes = new Dictionary<int, XsdLeiauteNode>();
        foreach (var (n, sufixo) in AncorasArquiteto)
        {
            var node = xsd.Nodes.FirstOrDefault(x =>
                x.XPath.EndsWith("/infNFe/" + sufixo, StringComparison.Ordinal));
            if (node is not null) anchorNodes[n] = node;
            else log?.Invoke($"   [aviso] âncora #{n} ({sufixo}) não achada no XSD.");
        }
        foreach (var w in works.Values)
        {
            if (w.Categoria == RefCategoria.PuroNumerico
                && int.TryParse(w.Key, out var n) && anchorNodes.TryGetValue(n, out var node))
            {
                w.AnchorNode = node;
            }
        }

        // ── 3. Pins de ordem (âncoras + value-pins numéricos), monotônicos ───
        var pins = OrderPins(works, anchorNodes, xsd);

        // ── 4. Interpolação por ordem (SÓ janelas exatas) ────────────────────
        foreach (var w in works.Values.Where(w =>
                     w.Categoria == RefCategoria.PuroNumerico && w.AnchorNode is null))
        {
            if (!int.TryParse(w.Key, out var n)) continue;
            var (prev, next) = Bracket(pins, n);
            if (prev is null || next is null) continue;
            var (n1, i1) = prev.Value;
            var (n2, i2) = next.Value;
            if (n2 - n1 > MaxJanelaInterp) continue;
            if (i2 - i1 != n2 - n1) continue;              // janela precisa ser EXATA
            w.OrderNode = xsd.Nodes[i1 + (n - n1)];
        }

        // ── 5. Semântica para numéricos puros (janela entre pins) ────────────
        foreach (var w in works.Values.Where(w => w.Categoria == RefCategoria.PuroNumerico))
        {
            if (!int.TryParse(w.Key, out var n)) continue;
            var (prev, next) = Bracket(pins, n);
            var candidates = prev is not null && next is not null
                ? Window(leafNodes, prev.Value.Order, next.Value.Order)
                : leafNodes;
            w.Semantic = BestSemantic(w, candidates, idf);

            if (w.Key == debugRef)
            {
                log?.Invoke($"   [debug #{w.Key}] bracket=({prev?.N}@{prev?.Order}, {next?.N}@{next?.Order}) "
                    + $"janela={candidates.Count} nós; valuePaths={w.ValuePaths.Count} "
                    + $"[{string.Join(", ", w.ValuePaths.Take(3))}]");
                foreach (var f in w.Fields)
                {
                    var dt = SemanticMatcher.Tokens(f.FieldName ?? "");
                    var top = candidates
                        .Select(c => (c.XPath, S: SemanticMatcher.Score(dt, [], c, idf)))
                        .OrderByDescending(x => x.S).Take(5);
                    log?.Invoke($"   [debug #{w.Key}] desc='{f.FieldName}' tokens=[{string.Join(",", dt)}]");
                    foreach (var (xp, s) in top) log?.Invoke($"      {s:F3}  {xp}");
                }
            }
        }

        // ── 6. Finaliza numéricos puros e estende os pins com o que resolveu ─
        var results = new Dictionary<string, CatalogResolution>(StringComparer.Ordinal);
        foreach (var w in works.Values.Where(w => w.Categoria == RefCategoria.PuroNumerico))
            results[w.Key] = Combine(w, xsd);

        var pinsEstendidos = ExtendPins(pins, results, xsd);

        // ── 7. Sufixados (29a, 104.08, 324.107): janela a partir do nº-base ──
        foreach (var w in works.Values.Where(w =>
                     w.Categoria is RefCategoria.LetraSufixo or RefCategoria.SubRef))
        {
            var candidates = SuffixWindow(w.Key, results, pinsEstendidos, xsd, leafNodes);
            w.Semantic = BestSemantic(w, candidates, idf)
                      ?? BestSemantic(w, leafNodes, idf, minScore: 0.55);   // fallback global, limiar maior
            results[w.Key] = Combine(w, xsd);
        }

        // ── 8. Esquema MOC (B25c, YA04, R07…): semântica na subárvore do grupo ─
        foreach (var w in works.Values.Where(w => w.Categoria == RefCategoria.MocId))
        {
            var candidates = MocSubtree(w.Key, xsd) ?? leafNodes;
            w.Semantic = BestSemantic(w, candidates, idf);
            results[w.Key] = Combine(w, xsd);
        }

        // ── 9. Multi-refs (choice ICMS): folha comum → XPath curinga ─────────
        foreach (var w in works.Values.Where(w => w.Categoria == RefCategoria.MultiRef))
            results[w.Key] = ResolveMultiRef(w, leafNodes, idf, xsd);

        // ── 10. Extensões proprietárias (FIAT/ANFAVEA) ───────────────────────
        foreach (var w in works.Values.Where(w => w.Categoria == RefCategoria.Extensao))
        {
            // Só um value-pin único (em dadosAdic) dá XPath; senão fica honesto: fora do XSD.
            var pin = w.ValuePaths.Count == 1 ? w.ValuePaths[0] : null;
            results[w.Key] = new CatalogResolution(
                w.Key, RefCategoria.Extensao, pin, "", "",
                pin is null ? SinalResolucao.Nenhum : SinalResolucao.ValueAnchor,
                pin is null ? NivelConfianca.NaoResolvido : NivelConfianca.Media,
                ForaDoXsd: true, w.Fields.Count, w.Descricao(),
                "extensão proprietária (dadosAdic) — não pertence ao leiaute SEFAZ");
        }

        // ── Monta os índices de consulta ─────────────────────────────────────
        var resolved = new Dictionary<string, LeiauteEntry>(StringComparer.Ordinal);
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in works.Values)
        {
            foreach (var original in w.Originals)
                alias[original] = w.Key;
            if (results.TryGetValue(w.Key, out var r) && r.XPath is not null)
                resolved[w.Key] = new LeiauteEntry(w.Key, r.XPath, r.Tipo, r.Occurs);
        }

        var ordered = results.Values
            .OrderBy(r => r.Categoria)
            .ThenBy(r => r.XmlRef, StringComparer.Ordinal)
            .ToList();
        return new NfeLeiauteCatalog(ordered, resolved, alias);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Normalização e classificação dos refs
    // ══════════════════════════════════════════════════════════════════════

    private sealed class RefWork
    {
        public required string Key { get; init; }
        public required RefCategoria Categoria { get; init; }
        public HashSet<string> Originals { get; } = new(StringComparer.Ordinal);
        public string[] Parts { get; init; } = [];
        public List<SpecField> Fields { get; } = new();
        public List<string?> Contextos { get; } = new();
        public List<string> ValuePaths { get; set; } = new();
        public SemanticMatch? Semantic { get; set; }
        public XsdLeiauteNode? OrderNode { get; set; }
        public XsdLeiauteNode? AnchorNode { get; set; }

        public string Descricao() =>
            Fields.Select(f => f.FieldName).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "";
    }

    private static Dictionary<string, RefWork> CollectRefs(
        SpecModel spec, IReadOnlyDictionary<string, string> contextos)
    {
        // Bases com família pontuada ("324." existe) — para consertar "324107" → "324.107".
        var dottedBases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in spec.Blocks.SelectMany(b => b.Fields))
        {
            if (f.XmlRef is null) continue;
            foreach (var p in SplitParts(f.XmlRef))
            {
                var m = Regex.Match(p, @"^(\d+)[.,]");
                if (m.Success) dottedBases.Add(m.Groups[1].Value);
            }
        }

        var works = new Dictionary<string, RefWork>(StringComparer.Ordinal);
        foreach (var block in spec.Blocks)
        {
            contextos.TryGetValue(block.Name, out var ctx);
            foreach (var f in block.Fields)
            {
                if (f.XmlRef is null) continue;
                var parts = SplitParts(f.XmlRef).Select(p => NormalizePart(p, dottedBases)).ToArray();
                var key = string.Join("|", parts);
                var categoria = parts.Length > 1 ? RefCategoria.MultiRef : Categorize(parts[0]);

                if (!works.TryGetValue(key, out var w))
                    works[key] = w = new RefWork { Key = key, Categoria = categoria, Parts = parts };
                w.Originals.Add(f.XmlRef.Trim());
                w.Fields.Add(f);
                w.Contextos.Add(ctx);
            }
        }
        return works;
    }

    private static List<string> SplitParts(string raw)
    {
        var parts = raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        // Lista MOC separada por espaços num único ref ("N04 N05 N06  N09 N10").
        if (parts.Count == 1 && parts[0].Contains(' ') && char.IsLetter(parts[0][0]))
            parts = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        return parts;
    }

    private static string NormalizePart(string p, IReadOnlySet<string> dottedBases)
    {
        p = p.Trim();
        p = Regex.Replace(p, @"^(\d+),(\d+)$", "$1.$2");            // "245,08" → "245.08"
        if (Regex.IsMatch(p, @"^\d{5,6}$") && dottedBases.Contains(p[..3]))
            p = $"{p[..3]}.{p[3..]}";                               // "324107" → "324.107"
        return p;
    }

    private static RefCategoria Categorize(string p) => p switch
    {
        "FIAT" or "ANFAVEA" => RefCategoria.Extensao,
        _ when Regex.IsMatch(p, @"^\d+$") => RefCategoria.PuroNumerico,
        _ when Regex.IsMatch(p, @"^\d+\.") => RefCategoria.SubRef,          // 104.08, 245.14b
        _ when Regex.IsMatch(p, @"^\d+[a-zA-Z]") => RefCategoria.LetraSufixo, // 29a, 29x.4, 398a2
        _ when Regex.IsMatch(p, @"^[A-Z]") => RefCategoria.MocId,           // B25c, YA04, R07
        _ => RefCategoria.Outro
    };

    /// <summary>Contexto semântico do bloco = título "Bloco-NNN - …" (1º campo do bloco).</summary>
    private static Dictionary<string, string> BlockContexts(SpecModel spec)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in spec.Blocks)
        {
            var titulo = b.Fields
                .Select(f => f.FieldName)
                .FirstOrDefault(n => n is not null && Regex.IsMatch(n, @"^Bloco[-\s]\d+"));
            if (titulo is not null) map[b.Name] = titulo;
        }
        return map;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Camada 1 — pins e janelas de ordem
    // ══════════════════════════════════════════════════════════════════════

    private static List<(int N, int Order)> OrderPins(
        Dictionary<string, RefWork> works,
        Dictionary<int, XsdLeiauteNode> anchorNodes,
        XsdLeiauteIndex xsd)
    {
        var pins = new Dictionary<int, int>();
        foreach (var (n, node) in anchorNodes) pins[n] = node.Order;

        // Value-pins de refs numéricos puros: XPath único E existente no XSD.
        foreach (var w in works.Values)
        {
            if (w.Categoria != RefCategoria.PuroNumerico) continue;
            if (!int.TryParse(w.Key, out var n) || pins.ContainsKey(n)) continue;
            if (w.ValuePaths.Count == 1 && xsd.TryByPath(w.ValuePaths[0], out var node))
                pins[n] = node.Order;
        }

        return MonotonicSubset(pins.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList());
    }

    /// <summary>
    /// Maior subsequência crescente em Order (LIS) — descarta pins inconsistentes
    /// com a monotonicidade (a numeração legada × ordem 4.00 diverge no choice
    /// ICMS×ISSQN, por exemplo). Determinístico.
    /// </summary>
    private static List<(int N, int Order)> MonotonicSubset(List<(int N, int Order)> pins)
    {
        if (pins.Count == 0) return pins;
        var best = new int[pins.Count];
        var prev = new int[pins.Count];
        for (var i = 0; i < pins.Count; i++)
        {
            best[i] = 1; prev[i] = -1;
            for (var j = 0; j < i; j++)
            {
                if (pins[j].Order < pins[i].Order && best[j] + 1 > best[i])
                {
                    best[i] = best[j] + 1;
                    prev[i] = j;
                }
            }
        }
        var end = Array.IndexOf(best, best.Max());
        var keep = new List<(int, int)>();
        for (var i = end; i >= 0; i = prev[i]) keep.Add(pins[i]);
        keep.Reverse();
        return keep;
    }

    private static ((int N, int Order)? Prev, (int N, int Order)? Next) Bracket(
        List<(int N, int Order)> pins, int n)
    {
        (int, int)? prev = null, next = null;
        foreach (var p in pins)
        {
            if (p.N < n) prev = p;
            else if (p.N > n) { next = p; break; }
            else return (p, p);   // o próprio nº é pin (âncora) — não interpola
        }
        return (prev, next);
    }

    private static List<XsdLeiauteNode> Window(
        IReadOnlyList<XsdLeiauteNode> leafNodes, int orderIni, int orderFim) =>
        leafNodes.Where(x => x.Order > orderIni && x.Order < orderFim).ToList();

    private static List<(int N, int Order)> ExtendPins(
        List<(int N, int Order)> pins,
        Dictionary<string, CatalogResolution> results,
        XsdLeiauteIndex xsd)
    {
        var all = pins.ToDictionary(p => p.N, p => p.Order);
        foreach (var r in results.Values)
        {
            if (r.XPath is null || r.ForaDoXsd || !int.TryParse(r.XmlRef, out var n)) continue;
            if (!all.ContainsKey(n) && xsd.TryByPath(r.XPath, out var node))
                all[n] = node.Order;
        }
        return MonotonicSubset(all.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList());
    }

    /// <summary>
    /// Janela de candidatos para ref sufixado: campos inseridos por NT vivem
    /// entre o nº-base e o próximo nº legado resolvido (com teto de segurança).
    /// </summary>
    private static IReadOnlyList<XsdLeiauteNode>? SuffixWindow(
        string key,
        Dictionary<string, CatalogResolution> results,
        List<(int N, int Order)> pins,
        XsdLeiauteIndex xsd,
        IReadOnlyList<XsdLeiauteNode> leafNodes)
    {
        var m = Regex.Match(key, @"^(\d+)");
        if (!m.Success) return null;
        var baseN = int.Parse(m.Groups[1].Value);

        // Ordem do nó-base: pelo pin ou pela resolução final do nº puro.
        int? baseOrder = pins.Where(p => p.N == baseN).Select(p => (int?)p.Order).FirstOrDefault();
        if (baseOrder is null
            && results.TryGetValue(baseN.ToString(), out var rBase)
            && rBase.XPath is not null && !rBase.ForaDoXsd
            && xsd.TryByPath(rBase.XPath, out var baseNode))
        {
            baseOrder = baseNode.Order;
        }
        if (baseOrder is null) return null;

        var next = pins.FirstOrDefault(p => p.N > baseN && p.Order > baseOrder.Value);
        var fim = next == default ? baseOrder.Value + MaxJanelaSufixo
                                  : Math.Min(next.Order, baseOrder.Value + MaxJanelaSufixo);
        var win = Window(leafNodes, baseOrder.Value, fim);
        return win.Count > 0 ? win : null;
    }

    private static IReadOnlyList<XsdLeiauteNode>? MocSubtree(string key, XsdLeiauteIndex xsd)
    {
        var letras = Regex.Match(key, @"^[A-Z]+").Value;
        foreach (var (prefixo, sufixo) in GruposMoc)
        {
            if (!letras.StartsWith(prefixo, StringComparison.Ordinal)) continue;
            var grupo = xsd.GroupBySuffix(sufixo);
            if (grupo is null) return null;
            var sub = xsd.Subtree(grupo.XPath + "/").Where(n => !n.IsGroup).ToList();
            return sub.Count > 0 ? sub : null;
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Camada 2 — semântica (com voto entre descrições divergentes)
    // ══════════════════════════════════════════════════════════════════════

    private static SemanticMatch? BestSemantic(
        RefWork w, IReadOnlyList<XsdLeiauteNode>? candidates,
        IReadOnlyDictionary<string, double> idf, double minScore = SemanticMatcher.MinScore)
    {
        if (candidates is null || candidates.Count == 0) return null;

        SemanticMatch? best = null;
        for (var i = 0; i < w.Fields.Count; i++)
        {
            var desc = w.Fields[i].FieldName;
            if (string.IsNullOrWhiteSpace(desc)) continue;
            var match = SemanticMatcher.Best(desc, w.Contextos[i], candidates, idf, minScore);
            if (match is not null && (best is null || match.Score > best.Score))
                best = match;
        }
        return best;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Combinação de sinais → resolução final com confiança honesta
    // ══════════════════════════════════════════════════════════════════════

    private static CatalogResolution Combine(RefWork w, XsdLeiauteIndex xsd)
    {
        var strongValue = w.ValuePaths.Count == 1 ? w.ValuePaths[0] : null;
        var semanticPath = w.Semantic?.Node.XPath;
        var orderPath = (w.AnchorNode ?? w.OrderNode)?.XPath;

        string? winner = null;
        var sinais = SinalResolucao.Nenhum;
        string? obs = null;
        var conflito = false;

        if (strongValue is not null)
        {
            winner = strongValue;
            sinais |= SinalResolucao.ValueAnchor;
            if (semanticPath == winner) sinais |= SinalResolucao.Semantic;
            else if (semanticPath is not null) { conflito = true; obs = $"semântica divergiu ({semanticPath})"; }
            if (orderPath == winner) sinais |= SinalResolucao.XsdOrder;
        }
        else if (orderPath is not null && semanticPath is not null)
        {
            if (orderPath == semanticPath)
            {
                winner = orderPath;
                sinais = SinalResolucao.XsdOrder | SinalResolucao.Semantic;
            }
            else
            {
                // Conflito ordem×semântica: docs PT-BR são o sinal mais forte dos dois.
                winner = semanticPath;
                sinais = SinalResolucao.Semantic;
                conflito = true;
                obs = $"ordem divergiu ({orderPath})";
            }
            if (w.ValuePaths.Contains(winner!)) sinais |= SinalResolucao.ValueAnchor;
        }
        else if (semanticPath is not null || orderPath is not null)
        {
            winner = semanticPath ?? orderPath;
            sinais = semanticPath is not null ? SinalResolucao.Semantic : SinalResolucao.XsdOrder;
            // Value fraco (2+ paths) conta como concordância se contiver o vencedor.
            if (w.ValuePaths.Count > 1 && w.ValuePaths.Contains(winner!))
                sinais |= SinalResolucao.ValueAnchor;
        }
        else if (w.ValuePaths.Count > 1)
        {
            // Resgate por NOME LITERAL: a spec às vezes usa o próprio nome da tag
            // como descrição ("cEAN", "cEANTrib") — se exatamente UM dos XPaths
            // ambíguos do valor termina nesse nome, o empate está desfeito.
            var literal = LiteralNameRescue(w);
            if (literal is not null)
            {
                winner = literal;
                sinais = SinalResolucao.ValueAnchor | SinalResolucao.Semantic;
                obs = "desambiguado pelo nome literal da folha na descrição";
            }
            else
            {
                obs = $"valor ambíguo entre {w.ValuePaths.Count} XPaths ({string.Join(", ", w.ValuePaths.Take(3))}…)";
            }
        }

        if (winner is null)
        {
            return new CatalogResolution(
                w.Key, w.Categoria, null, "", "", SinalResolucao.Nenhum,
                NivelConfianca.NaoResolvido, false, w.Fields.Count, w.Descricao(), obs);
        }

        if (w.Semantic is { TiedCount: > 1 } tie && winner == tie.Node.XPath)
            obs = Append(obs, $"{tie.TiedCount} variantes homônimas no choice (mesma doc)");

        var foraDoXsd = !xsd.TryByPath(winner, out var node);
        var nSinais = CountSignals(sinais);
        var confianca = conflito ? NivelConfianca.Baixa
                      : nSinais >= 2 ? NivelConfianca.Alta
                      : NivelConfianca.Media;

        return new CatalogResolution(
            w.Key, w.Categoria, winner,
            foraDoXsd ? "" : node.TypeName,
            foraDoXsd ? "" : node.Occurs,
            sinais, confianca, foraDoXsd, w.Fields.Count, w.Descricao(),
            foraDoXsd ? Append(obs, "XPath ancorado fora do XSD (extensão dadosAdic)") : obs);
    }

    /// <summary>
    /// Multi-ref (um campo TXT → mesma folha em N variantes do choice ICMS):
    /// elege a FOLHA por nome comum via semântica e devolve XPath curinga
    /// ("…/ICMS/*/pICMS") quando as variantes compartilham o mesmo avô.
    /// </summary>
    private static CatalogResolution ResolveMultiRef(
        RefWork w, IReadOnlyList<XsdLeiauteNode> leafNodes,
        IReadOnlyDictionary<string, double> idf, XsdLeiauteIndex xsd)
    {
        var match = BestSemanticByName(w, leafNodes, idf);
        if (match is null)
        {
            return new CatalogResolution(
                w.Key, RefCategoria.MultiRef, null, "", "", SinalResolucao.Nenhum,
                NivelConfianca.NaoResolvido, false, w.Fields.Count, w.Descricao(),
                $"multi-ref ({w.Parts.Length} variantes do choice) sem folha comum resolvida");
        }

        var (nodes, _) = match.Value;
        string xpath;
        string occurs;
        if (nodes.Count == 1)
        {
            xpath = nodes[0].XPath;
            occurs = nodes[0].Occurs;
        }
        else
        {
            // Curinga: prefixo comum + /*/ + nome (todas as variantes têm o mesmo avô).
            var prefix = CommonPrefix(nodes.Select(n => n.XPath).ToList());
            xpath = $"{prefix}/*/{nodes[0].Name}";
            occurs = "choice";
        }

        var sinais = SinalResolucao.Semantic;
        if (w.ValuePaths.Count == 1 && nodes.Any(n => n.XPath == w.ValuePaths[0]))
            sinais |= SinalResolucao.ValueAnchor;

        return new CatalogResolution(
            w.Key, RefCategoria.MultiRef, xpath, nodes[0].TypeName, occurs, sinais,
            CountSignals(sinais) >= 2 ? NivelConfianca.Alta : NivelConfianca.Media,
            false, w.Fields.Count, w.Descricao(),
            $"folha comum em {nodes.Count} variante(s) do choice");
    }

    /// <summary>Melhor NOME de folha (score máximo por nome) — para multi-refs.</summary>
    private static (IReadOnlyList<XsdLeiauteNode> Nodes, double Score)? BestSemanticByName(
        RefWork w, IReadOnlyList<XsdLeiauteNode> leafNodes, IReadOnlyDictionary<string, double> idf)
    {
        var desc = w.Descricao();
        if (desc.Length == 0) return null;
        var descTokens = SemanticMatcher.Tokens(desc);
        if (descTokens.Count == 0) return null;

        var porNome = new Dictionary<string, (List<XsdLeiauteNode> Nodes, double Max)>(StringComparer.Ordinal);
        foreach (var node in leafNodes)
        {
            var s = SemanticMatcher.Score(descTokens, [], node, idf);
            if (s <= 0) continue;
            if (!porNome.TryGetValue(node.Name, out var acc))
                porNome[node.Name] = acc = (new List<XsdLeiauteNode>(), 0);
            if (s >= acc.Max - 1e-9)
            {
                // Guarda os nós que empatam no score máximo do nome (as variantes).
                if (s > acc.Max + 1e-9) acc.Nodes.Clear();
                acc.Nodes.Add(node);
                porNome[node.Name] = (acc.Nodes, Math.Max(acc.Max, s));
            }
        }
        if (porNome.Count == 0) return null;

        var ranked = porNome.OrderByDescending(kv => kv.Value.Max).ToList();
        var best = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Value.Max : 0;
        if (best.Value.Max < SemanticMatcher.MinScore) return null;
        if (best.Value.Max - second < SemanticMatcher.MinMargin) return null;

        // Curinga só faz sentido se as variantes compartilham o avô (prefixo comum + 2 níveis).
        var nodes = best.Value.Nodes;
        if (nodes.Count > 1)
        {
            var prefix = CommonPrefix(nodes.Select(n => n.XPath).ToList());
            if (nodes.Any(n => n.XPath.Count(c => c == '/') != prefix.Count(c => c == '/') + 2))
                nodes = [nodes[0]];   // estrutura heterogênea → fica no melhor nó único
        }
        return (nodes, best.Value.Max);
    }

    private static string CommonPrefix(List<string> paths)
    {
        var segs = paths[0].Split('/');
        var len = segs.Length;
        foreach (var p in paths.Skip(1))
        {
            var s = p.Split('/');
            var i = 0;
            while (i < len && i < s.Length && segs[i] == s[i]) i++;
            len = i;
        }
        return string.Join('/', segs[..len]);
    }

    /// <summary>
    /// Se a descrição da spec É o nome de uma folha (case-insensitive) e exatamente
    /// um dos XPaths ambíguos do valor termina nela, devolve esse XPath.
    /// </summary>
    private static string? LiteralNameRescue(RefWork w)
    {
        foreach (var f in w.Fields)
        {
            var desc = f.FieldName?.Trim();
            if (string.IsNullOrEmpty(desc) || desc.Contains(' ')) continue;
            var hits = w.ValuePaths
                .Where(p => p[(p.LastIndexOf('/') + 1)..]
                    .Equals(desc, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 1) return hits[0];
        }
        return null;
    }

    private static int CountSignals(SinalResolucao s)
    {
        var n = 0;
        if (s.HasFlag(SinalResolucao.XsdOrder)) n++;
        if (s.HasFlag(SinalResolucao.Semantic)) n++;
        if (s.HasFlag(SinalResolucao.ValueAnchor)) n++;
        return n;
    }

    private static string Append(string? obs, string extra) =>
        obs is null ? extra : $"{obs}; {extra}";
}
