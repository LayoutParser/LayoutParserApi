using System.Xml.Linq;

namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// XslGenerator — PoC-3/A2: SpecModel + NfeLeiauteCatalog → XSL (ROOT → NF-e).
//
// 100% determinístico (PROIBIDO LLM nesta PoC). Usa só resoluções Alta/Média do
// catálogo; campo não resolvido tenta a ancoragem por VALOR direta (camada 3 do
// catálogo aplicada por campo) e, falhando, vira gap honesto no relatório.
//
// Regras de valor (§7.3 do desenho — descobertas no gabarito):
//   1. zeros à esquerda: strip por TIPO XSD (TSerie '006'→'6', TNF '000150839'→'150839');
//   2. decimais: inserção de ponto POR STRING (substring/concat) — sem divisão em
//      double, para não perder precisão em 10 casas (vUnCom);
//   3. datas: AAAAMMDD → AAAA-MM-DD por substring; AAAA-MM-DD/ISO passa direto;
//   4. opcionais vazios OMITIDOS: toda folha é condicionada; grupos opcionais são
//      envolvidos num xsl:if com o OR dos testes das folhas internas;
//   5. infCpl = concat dos segmentos das LINHA081 repetidas (trim, SEM separador).
//
// Choices de imposto (§7.4): decididos EM RUNTIME por xsl:choose sobre o CST —
// ICMS (mapa CST→ICMSxx), IPI (00/49/50/99→IPITrib, senão IPINT), PIS/COFINS
// (01-02→Aliq, 03→Qtde, 04-09→NT, senão Outr). Os refs SubRef/MultiRef são
// reancorados PELO NOME DA FOLHA dentro da variante escolhida.
//
// Consistência de região (determinística): campo de bloco não-det que resolveu
// para /det/ (ou vice-versa) é reancorado quando existe exatamente UMA folha de
// mesmo nome na região correta (ex.: pesoL da LINHA056 → transp/vol/pesoL, não
// det/prod/veicProd/pesoL); sem folha única, é descartado com nota honesta.
//
// Desenho: docs/architecture/poc-excel-generator.md §7.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Relatório da geração (contagens + notas honestas de gaps/decisões).</summary>
public sealed record XslGenReport(
    XDocument Xsl,
    int CamposComRef,
    int FolhasEmitidas,
    int CamposDescartados,
    IReadOnlyList<string> Notas);

/// <summary>Gera o XSL determinístico ROOT → NF-e a partir da spec + catálogo.</summary>
public sealed class XslGenerator
{
    private static readonly XNamespace Xs = Core.Xslt.Ns;

    private const string InfNFePath = "enviNFe/NFe/infNFe";
    private const string DetPath = "enviNFe/NFe/infNFe/det";
    private const string ImpostoPath = "enviNFe/NFe/infNFe/det/imposto";

    // §7.4 — seleção da variante ICMS pelo CST (CSOSN fica no otherwise: driver
    // CSOSN não resolvido no catálogo; estruturar quando houver gabarito SN).
    private static readonly (string Variante, string[] Csts)[] IcmsPorCst =
    [
        ("ICMS00", ["00"]), ("ICMS10", ["10"]), ("ICMS20", ["20"]), ("ICMS30", ["30"]),
        ("ICMS40", ["40", "41", "50"]), ("ICMS51", ["51"]), ("ICMS60", ["60"]),
        ("ICMS70", ["70"]), ("ICMS90", ["90"])
    ];

    // IPI: CSTs tributados → IPITrib; demais (não tributados) → IPINT.
    private static readonly string[] IpiTribCsts = ["00", "49", "50", "99"];

    // PIS/COFINS: regra padrão do leiaute NF-e por faixa de CST.
    private static readonly (string SufixoVariante, string[] Csts)[] PisCofinsPorCst =
    [
        ("Aliq", ["01", "02"]), ("Qtde", ["03"]), ("NT", ["04", "05", "06", "07", "08", "09"])
    ];

    // Tipos XSD cujo léxico PROÍBE zeros à esquerda (regra §7.3.1).
    private static readonly HashSet<string> StripTypes = new(StringComparer.Ordinal)
        { "TSerie", "TNF" };

    // Tipos-quantidade em que zero significa "não informado" (PISST/COFINSST,
    // qBCProd/vAliqProd) — diferente dos TDec_13xx/03xx, onde 0.00 é emitido
    // (ICMSTot, pIPI…). Regra empírica do gabarito, por TIPO (não por campo).
    private static readonly HashSet<string> ZeroOmitTypes = new(StringComparer.Ordinal)
        { "TDec_1104", "TDec_1204" };

    private readonly List<string> _notas = new();
    private int _descartados;

    private XsdLeiauteIndex _xsd = null!;
    private XDocument _rootTree = null!;

    public XslGenReport Generate(
        SpecModel spec,
        NfeLeiauteCatalog catalog,
        XsdLeiauteIndex xsd,
        NfeGabarito? gabarito,
        XDocument rootTree,
        Action<string>? log = null)
    {
        _xsd = xsd;
        _rootTree = rootTree;
        _notas.Clear();
        _descartados = 0;

        var versao = VersaoDoLeiaute(spec.SheetName);
        var resByKey = catalog.Resolutions.ToDictionary(r => r.XmlRef, StringComparer.Ordinal);

        // ── 1. Coleta os usos campo→XPath (Alta/Média; fallback por valor) ────
        var usos = ColetarUsos(spec, catalog, resByKey, gabarito, out var camposComRef);

        // ── 2. Blocos de item (det) por votação dos destinos resolvidos ──────
        var detBlocks = DetectarBlocosDet(usos);
        foreach (var u in usos) u.DetBlock = detBlocks.Contains(u.B.Name);

        // ── 3. Consistência de região (reancora ou descarta) ─────────────────
        usos = AjustarRegioes(usos);

        // ── 4. Separa membros de choice de imposto × folhas normais ──────────
        var (folhas, membros) = SepararChoices(usos);

        // ── 5. Dedup de folhas por destino (região não-repetida) ─────────────
        folhas = DedupPorDestino(folhas);

        // ── 6. Emissões de topo e de item ─────────────────────────────────────
        var masterDet = folhas.Where(u => u.DetBlock)
            .OrderBy(u => u.Path.EndsWith("/prod/cProd", StringComparison.Ordinal) ? 0 : 1)
            .Select(u => u.B.Name)
            .FirstOrDefault() ?? detBlocks.FirstOrDefault() ?? "LINHA020";

        var topo = new List<Emissao>();
        var det = new List<Emissao>();

        // Especiais primeiro: infCpl (§7.3.5) DEDUPLICA a lista de folhas antes
        // da montagem; o atributo Id vem da chave de acesso (44 chars).
        var infCpl = EmitirInfCpl(spec, folhas);
        if (infCpl is not null) topo.Add(infCpl);
        var id = EmitirAtributoId(spec, detBlocks);
        if (id is not null) topo.Add(id);

        foreach (var u in folhas)
        {
            if (!_xsd.TryByPath(u.Path, out var node) || node.IsGroup)
            {
                Nota($"#{u.F.XmlRef}: destino '{u.Path}' não é folha do XSD — descartado.");
                continue;
            }
            var scopeDet = u.Path.StartsWith(DetPath + "/", StringComparison.Ordinal);
            var sel = scopeDet ? SelDet(u, masterDet) : SelTop(u);
            var (expr, test) = ValorETeste(u.F, node, sel);
            var conteudo = new XElement(node.Name, ValueOf(expr));
            (scopeDet ? det : topo).Add(new Emissao(node.Order, ParentOf(u.Path), conteudo, test));
        }

        // Choices de imposto (dentro do escopo det).
        det.AddRange(EmitirChoices(membros, masterDet));

        // ── 7. Monta o stylesheet ─────────────────────────────────────────────
        var enviNFe = new XElement("enviNFe", new XAttribute("versao", versao));
        MontarEscopo(enviNFe, "enviNFe", topo);

        var infNFe = Achar(enviNFe, "NFe", "infNFe");
        infNFe?.Add(new XAttribute("versao", versao));

        // Bloco det: for-each sobre a linha-mestre; irmãs correlacionadas por
        // ORDINAL ($i = posição do item) — suficiente para o par real (1 item);
        // multi-item com linhas opcionais fica para a generalização (nota §7.5).
        if (det.Count > 0 && infNFe is not null)
        {
            var forEach = new XElement(Xs + "for-each",
                new XAttribute("select", $"ROOT/{masterDet}"),
                new XElement(Xs + "variable",
                    new XAttribute("name", "i"), new XAttribute("select", "position()")));

            var detEl = new XElement("det",
                new XElement(Xs + "attribute", new XAttribute("name", "nItem"),
                    new XElement(Xs + "value-of", new XAttribute("select", "position()"))));
            MontarEscopo(detEl, DetPath, det);
            forEach.Add(detEl);

            // Insere o for-each na posição do grupo det (ordem do documento XSD).
            var detNode = _xsd.TryByPath(DetPath, out var dn) ? dn.Order : int.MaxValue;
            InserirNaOrdem(infNFe, forEach, detNode, InfNFePath);
        }

        // Grupos OPCIONAIS com todos os filhos condicionais → xsl:if com OR.
        EmbrulharGruposOpcionais(enviNFe, "enviNFe");

        var doc = new XDocument(
            new XElement(Xs + "stylesheet",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsl", Xs.NamespaceName),
                new XElement(Xs + "output",
                    new XAttribute("method", "xml"),
                    new XAttribute("encoding", "utf-8"),
                    new XAttribute("indent", "no")),
                new XElement(Xs + "template", new XAttribute("match", "/"), enviNFe)));

        var emitidas = doc.Descendants(Xs + "value-of").Count();
        foreach (var n in _notas.Take(30)) log?.Invoke($"   [nota] {n}");
        if (_notas.Count > 30) log?.Invoke($"   [nota] … e mais {_notas.Count - 30} notas.");

        return new XslGenReport(doc, camposComRef, emitidas, _descartados, _notas.ToList());
    }

    // ══════════════════════════════════════════════════════════════════════
    // 1. Coleta de usos
    // ══════════════════════════════════════════════════════════════════════

    private sealed class Uso
    {
        public required SpecField F { get; init; }
        public required SpecBlock B { get; init; }
        public required string Slug { get; init; }
        public required string Path { get; set; }
        public NivelConfianca Conf { get; init; }
        public SinalResolucao Sinais { get; init; }
        public bool DetBlock { get; set; }
        public int Ordem { get; init; }
    }

    private List<Uso> ColetarUsos(
        SpecModel spec,
        NfeLeiauteCatalog catalog,
        IReadOnlyDictionary<string, CatalogResolution> resByKey,
        NfeGabarito? gabarito,
        out int camposComRef)
    {
        var usos = new List<Uso>();
        camposComRef = 0;
        var ordem = 0;

        foreach (var block in spec.Blocks)
        {
            foreach (var (f, slug) in TclGenerator.NamedFields(block))
            {
                if (f.XmlRef is null) continue;
                camposComRef++;
                ordem++;

                if (catalog.TryResolve(f.XmlRef, out var entry)
                    && resByKey.TryGetValue(entry.XmlRef, out var r))
                {
                    if (r.ForaDoXsd)
                    {
                        Nota($"#{r.XmlRef}: extensão dadosAdic — fica para a Etapa B.");
                        continue;
                    }
                    if (r.Confianca is NivelConfianca.Baixa)
                    {
                        Nota($"#{r.XmlRef}: confiança BAIXA (sinais em conflito) — descartado.");
                        continue;
                    }
                    usos.Add(new Uso
                    {
                        F = f, B = block, Slug = slug, Path = entry.XPath,
                        Conf = r.Confianca, Sinais = r.Sinais, Ordem = ordem
                    });
                    continue;
                }

                // Fallback determinístico: ancoragem por VALOR direta (camada 3
                // do catálogo aplicada por campo — refs agregados podem diluir o pin).
                var paths = gabarito?.FindAnchorPaths(f) ?? [];
                var unico = paths.Count == 1 && _xsd.TryByPath(paths[0], out var n) && !n.IsAttribute
                    ? paths[0] : null;
                if (unico is not null)
                {
                    Nota($"#{f.XmlRef}: não resolvido no catálogo; ancorado por valor em {unico}.");
                    usos.Add(new Uso
                    {
                        F = f, B = block, Slug = slug, Path = unico,
                        Conf = NivelConfianca.Media, Sinais = SinalResolucao.ValueAnchor, Ordem = ordem
                    });
                }
                else
                {
                    _descartados++;
                }
            }
        }
        return usos;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 2/3. Região det × não-det
    // ══════════════════════════════════════════════════════════════════════

    private static HashSet<string> DetectarBlocosDet(List<Uso> usos)
    {
        var det = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in usos.GroupBy(u => u.B.Name))
        {
            var nDet = g.Count(u => u.Path.StartsWith(DetPath + "/", StringComparison.Ordinal));
            if (nDet > g.Count() - nDet) det.Add(g.Key);
        }
        return det;
    }

    private List<Uso> AjustarRegioes(List<Uso> usos)
    {
        var result = new List<Uso>(usos.Count);
        foreach (var u in usos)
        {
            var pathDet = u.Path.StartsWith(DetPath + "/", StringComparison.Ordinal);
            if (pathDet == u.DetBlock) { result.Add(u); continue; }

            var leaf = LeafName(u.Path);
            var candidatos = _xsd.Nodes
                .Where(n => !n.IsGroup && !n.IsAttribute
                    && n.Name == leaf
                    && n.XPath.StartsWith(InfNFePath + "/", StringComparison.Ordinal)
                    && n.XPath.StartsWith(DetPath + "/", StringComparison.Ordinal) == u.DetBlock)
                .ToList();

            if (candidatos.Count == 1)
            {
                Nota($"#{u.F.XmlRef}: reancorado por região ({u.Path} → {candidatos[0].XPath}).");
                u.Path = candidatos[0].XPath;
                result.Add(u);
            }
            else if (u.DetBlock && candidatos.Count > 1
                     && candidatos.All(c => c.XPath.StartsWith(ImpostoPath + "/ICMS/", StringComparison.Ordinal)))
            {
                // Folha comum das variantes ICMS → vira membro do choice (curinga).
                Nota($"#{u.F.XmlRef}: reancorado no choice ICMS pela folha '{leaf}'.");
                u.Path = $"{ImpostoPath}/ICMS/*/{leaf}";
                result.Add(u);
            }
            else
            {
                Nota($"#{u.F.XmlRef}: região do bloco ({(u.DetBlock ? "det" : "não-det")}) "
                    + $"não bate com o destino '{u.Path}' e não há folha única homônima — descartado.");
                _descartados++;
            }
        }
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 4. Choices de imposto
    // ══════════════════════════════════════════════════════════════════════

    private enum Regiao { Icms, Ipi, Pis, Cofins }

    private (List<Uso> Folhas, Dictionary<(Regiao, string), Uso> Membros) SepararChoices(List<Uso> usos)
    {
        var folhas = new List<Uso>();
        var membros = new Dictionary<(Regiao, string), Uso>();

        foreach (var u in usos)
        {
            // Curinga "imposto/*/…" (qBCProd PIS×COFINS×ST): variante indecidível
            // sem gabarito que a exercite — descarte honesto.
            if (u.Path.Contains("/imposto/*/", StringComparison.Ordinal))
            {
                Nota($"#{u.F.XmlRef}: destino curinga ambíguo '{u.Path}' — descartado.");
                _descartados++;
                continue;
            }

            var regiao = DetectarRegiao(u.Path);
            if (regiao is null) { folhas.Add(u); continue; }

            var leaf = LeafName(u.Path);
            var chave = (regiao.Value, leaf);
            if (!membros.TryGetValue(chave, out var atual) || Melhor(u, atual))
                membros[chave] = u;
        }
        return (folhas, membros);
    }

    private static Regiao? DetectarRegiao(string path)
    {
        if (!path.StartsWith(ImpostoPath + "/", StringComparison.Ordinal)) return null;
        var rel = path[(ImpostoPath.Length + 1)..];

        if (rel.StartsWith("ICMS/", StringComparison.Ordinal)) return Regiao.Icms;
        if (rel.StartsWith("IPI/IPITrib/", StringComparison.Ordinal)
            || rel.StartsWith("IPI/IPINT/", StringComparison.Ordinal)) return Regiao.Ipi;
        if (rel.StartsWith("PIS/", StringComparison.Ordinal)) return Regiao.Pis;
        if (rel.StartsWith("COFINS/", StringComparison.Ordinal)) return Regiao.Cofins;
        return null;
    }

    private IEnumerable<Emissao> EmitirChoices(
        Dictionary<(Regiao, string), Uso> membros, string masterDet)
    {
        var emissoes = new List<Emissao>();

        // ── ICMS ────────────────────────────────────────────────────────────
        var icms = Membros(membros, Regiao.Icms);
        if (icms.Count > 0)
            emissoes.Add(EmitirIcms(icms, masterDet));

        // ── IPI (só o choice IPITrib|IPINT; folhas diretas do IPI são normais) ─
        var ipi = Membros(membros, Regiao.Ipi);
        if (ipi.Count > 0)
            emissoes.Add(EmitirIpi(ipi, masterDet));

        // ── PIS e COFINS ────────────────────────────────────────────────────
        foreach (var (regiao, grupo) in new[] { (Regiao.Pis, "PIS"), (Regiao.Cofins, "COFINS") })
        {
            var m = Membros(membros, regiao);
            if (m.Count > 0)
                emissoes.Add(EmitirPisCofins(m, grupo, masterDet));
        }
        return emissoes;
    }

    private Dictionary<string, Uso> Membros(Dictionary<(Regiao, string), Uso> membros, Regiao r) =>
        membros.Where(kv => kv.Key.Item1 == r)
            .ToDictionary(kv => kv.Key.Item2, kv => kv.Value, StringComparer.Ordinal);

    private Emissao EmitirIcms(Dictionary<string, Uso> membros, string masterDet)
    {
        var icmsPath = ImpostoPath + "/ICMS";
        var driver = membros.TryGetValue("CST", out var d) ? SelDet(d, masterDet) : null;
        var icmsEl = new XElement("ICMS");

        if (driver is null)
        {
            icmsEl.Add(new XComment(" gap: CST do ICMS sem campo mapeado — variante indecidível "));
        }
        else
        {
            var choose = new XElement(Xs + "choose");
            foreach (var (variante, csts) in IcmsPorCst)
            {
                var vPath = $"{icmsPath}/{variante}";
                if (!_xsd.TryByPath(vPath, out _)) continue;
                var teste = string.Join(" or ", csts.Select(c => $"normalize-space({driver}) = '{c}'"));
                var el = new XElement(variante);
                PreencherVariante(el, vPath, membros, masterDet);
                choose.Add(new XElement(Xs + "when", new XAttribute("test", teste), el));
            }
            choose.Add(new XElement(Xs + "otherwise",
                new XComment(" CST/CSOSN sem variante mapeada nesta PoC (estruturar com gabarito SN) ")));
            icmsEl.Add(choose);
        }

        var order = _xsd.TryByPath(icmsPath, out var n) ? n.Order : int.MaxValue;
        var test = driver is null ? null : $"normalize-space({driver}) != ''";
        return new Emissao(order, ImpostoPath, icmsEl, test);
    }

    private Emissao EmitirIpi(Dictionary<string, Uso> membros, string masterDet)
    {
        var ipiPath = ImpostoPath + "/IPI";
        var driver = membros.TryGetValue("CST", out var d) ? SelDet(d, masterDet) : null;

        XElement conteudo;
        if (driver is null)
        {
            conteudo = new XElement(Xs + "comment",
                " gap: CST do IPI sem campo mapeado — IPITrib×IPINT indecidível ");
        }
        else
        {
            var trib = new XElement("IPITrib");
            PreencherVariante(trib, $"{ipiPath}/IPITrib", membros, masterDet);
            var nt = new XElement("IPINT");
            PreencherVariante(nt, $"{ipiPath}/IPINT", membros, masterDet);

            var teste = string.Join(" or ", IpiTribCsts.Select(c => $"normalize-space({driver}) = '{c}'"));
            conteudo = new XElement(Xs + "choose",
                new XElement(Xs + "when", new XAttribute("test", teste), trib),
                new XElement(Xs + "otherwise", nt));
        }

        // O choice entra na posição do IPITrib (1ª variante) DENTRO do grupo IPI,
        // que também recebe as folhas diretas (CNPJProd, cEnq…) como emissões normais.
        var order = _xsd.TryByPath($"{ipiPath}/IPITrib", out var n) ? n.Order : int.MaxValue;
        var test = driver is null ? null : $"normalize-space({driver}) != ''";
        return new Emissao(order, ipiPath, conteudo, test);
    }

    private Emissao EmitirPisCofins(Dictionary<string, Uso> membros, string grupo, string masterDet)
    {
        var grupoPath = $"{ImpostoPath}/{grupo}";
        var driver = membros.TryGetValue("CST", out var d) ? SelDet(d, masterDet) : null;
        var el = new XElement(grupo);

        if (driver is null)
        {
            el.Add(new XComment($" gap: CST do {grupo} sem campo mapeado — variante indecidível "));
        }
        else
        {
            var choose = new XElement(Xs + "choose");
            foreach (var (sufixo, csts) in PisCofinsPorCst)
            {
                var vPath = $"{grupoPath}/{grupo}{sufixo}";
                if (!_xsd.TryByPath(vPath, out _)) continue;
                var teste = string.Join(" or ", csts.Select(c => $"normalize-space({driver}) = '{c}'"));
                var v = new XElement($"{grupo}{sufixo}");
                PreencherVariante(v, vPath, membros, masterDet);
                choose.Add(new XElement(Xs + "when", new XAttribute("test", teste), v));
            }
            var outr = new XElement($"{grupo}Outr");
            PreencherVariante(outr, $"{grupoPath}/{grupo}Outr", membros, masterDet);
            choose.Add(new XElement(Xs + "otherwise", outr));
            el.Add(choose);
        }

        var order = _xsd.TryByPath(grupoPath, out var n) ? n.Order : int.MaxValue;
        var test = driver is null ? null : $"normalize-space({driver}) != ''";
        return new Emissao(order, ImpostoPath, el, test);
    }

    /// <summary>
    /// Preenche uma variante do choice com as folhas mapeadas, na ordem do XSD.
    /// O mesmo campo de origem alimenta a folha homônima da variante escolhida
    /// (§7.4). Folha 1-1 sem campo → comentário honesto.
    /// </summary>
    private void PreencherVariante(
        XElement variante, string vPath, Dictionary<string, Uso> membros, string masterDet)
    {
        foreach (var node in _xsd.Subtree(vPath + "/").Where(n => !n.IsGroup && !n.IsAttribute))
        {
            if (membros.TryGetValue(node.Name, out var u))
            {
                var sel = SelDet(u, masterDet);
                var (expr, test) = ValorETeste(u.F, node, sel);
                var folha = new XElement(node.Name, ValueOf(expr));
                variante.Add(new XElement(Xs + "if", new XAttribute("test", test), folha));
            }
            else if (node.Occurs.StartsWith('1'))
            {
                variante.Add(new XComment($" gap: {node.Name} (1-1) sem campo mapeado "));
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 5. Dedup por destino
    // ══════════════════════════════════════════════════════════════════════

    private List<Uso> DedupPorDestino(List<Uso> folhas)
    {
        var result = new List<Uso>(folhas.Count);
        foreach (var g in folhas.GroupBy(u => u.Path, StringComparer.Ordinal))
        {
            var vencedor = g.Aggregate((a, b) => Melhor(b, a) ? b : a);
            foreach (var perdedor in g.Where(u => !ReferenceEquals(u, vencedor)))
                Nota($"#{perdedor.F.XmlRef}: destino '{g.Key}' duplicado — vence #{vencedor.F.XmlRef}.");
            result.Add(vencedor);
        }
        return result;
    }

    /// <summary>
    /// Critério determinístico de desempate entre usos para o MESMO destino:
    /// confiança > nº de sinais > valor presente no ROOT real > ordem na spec.
    /// </summary>
    private bool Melhor(Uso a, Uso b)
    {
        if (a.Conf != b.Conf) return a.Conf > b.Conf;
        var (sa, sb) = (ContaSinais(a.Sinais), ContaSinais(b.Sinais));
        if (sa != sb) return sa > sb;
        var (va, vb) = (ProbeLen(a), ProbeLen(b));
        if (va != vb) return va > vb;
        return a.Ordem < b.Ordem;
    }

    /// <summary>Comprimento do valor REAL (trim) do campo na 1ª ocorrência do bloco no ROOT.</summary>
    private int ProbeLen(Uso u) =>
        _rootTree.Root?.Elements(u.B.Name).FirstOrDefault()
            ?.Element(u.Slug)?.Value.Trim().Length ?? 0;

    private static int ContaSinais(SinalResolucao s)
    {
        var n = 0;
        if (s.HasFlag(SinalResolucao.XsdOrder)) n++;
        if (s.HasFlag(SinalResolucao.Semantic)) n++;
        if (s.HasFlag(SinalResolucao.ValueAnchor)) n++;
        return n;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 6. Especiais (infCpl, atributo Id)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §7.3.5: infCpl = concat (trim, SEM separador) dos segmentos das linhas
    /// repetidas (LINHA081). O campo é achado deterministicamente: bloco com 2+
    /// ocorrências no ROOT cuja descrição contém "complementar".
    /// </summary>
    private Emissao? EmitirInfCpl(SpecModel spec, List<Uso> folhas)
    {
        const string infCplPath = "enviNFe/NFe/infNFe/infAdic/infCpl";
        if (!_xsd.TryByPath(infCplPath, out var node)) return null;

        var repetidos = _rootTree.Root!.Elements()
            .GroupBy(e => e.Name.LocalName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var block in spec.Blocks.Where(b => repetidos.Contains(b.Name)))
        {
            foreach (var (f, slug) in TclGenerator.NamedFields(block))
            {
                var desc = Fold(f.FieldName ?? "");
                if (!desc.Contains("complementar", StringComparison.OrdinalIgnoreCase)) continue;

                // Se algum uso normal já mira infCpl, o especial vence (dedup honesto).
                folhas.RemoveAll(u =>
                {
                    var bate = u.Path == infCplPath;
                    if (bate) Nota($"#{u.F.XmlRef}: infCpl coberto pela regra §7.3.5 (concat LINHA081).");
                    return bate;
                });

                var folha = new XElement(node.Name,
                    new XElement(Xs + "for-each",
                        new XAttribute("select", $"ROOT/{block.Name}"),
                        new XElement(Xs + "value-of",
                            new XAttribute("select", $"normalize-space({slug})"))));
                var teste = $"ROOT/{block.Name}[normalize-space({slug}) != '']";
                Nota($"infCpl: concat dos segmentos de {block.Name}/{slug} (regra §7.3.5).");
                return new Emissao(node.Order, ParentOf(infCplPath), folha, teste);
            }
        }
        return null;
    }

    /// <summary>
    /// Atributo obrigatório infNFe/@Id = 'NFe' + chave de acesso (44 dígitos).
    /// O campo-chave é o único da spec com Tamanho 44 fora da região det.
    /// </summary>
    private Emissao? EmitirAtributoId(SpecModel spec, HashSet<string> detBlocks)
    {
        const string idPath = "enviNFe/NFe/infNFe/@Id";
        if (!_xsd.TryByPath(idPath, out var node)) return null;

        foreach (var block in spec.Blocks.Where(b => !detBlocks.Contains(b.Name)))
        {
            foreach (var (f, slug) in TclGenerator.NamedFields(block))
            {
                if (f.Tamanho != 44) continue;
                if (!Fold(f.FieldName ?? "").Contains("chave", StringComparison.OrdinalIgnoreCase)) continue;

                var attr = new XElement(Xs + "attribute", new XAttribute("name", "Id"),
                    new XElement(Xs + "value-of",
                        new XAttribute("select", $"concat('NFe', normalize-space(ROOT/{block.Name}/{slug}))")));
                Nota($"@Id: NFe + {block.Name}/{slug} (campo de 44 chars com 'chave' na descrição).");
                return new Emissao(node.Order, InfNFePath, attr, Test: null);
            }
        }
        Nota("@Id: campo da chave de acesso (44 chars) NÃO encontrado — XSD acusará a falta.");
        return null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 7. Montagem (ordem do documento XSD) e embrulho de grupos opcionais
    // ══════════════════════════════════════════════════════════════════════

    /// <param name="Order">Ordem do nó-alvo no XSD (define a posição do irmão).</param>
    /// <param name="ParentPath">XPath do PAI no leiaute (grupos criados sob demanda).</param>
    /// <param name="Conteudo">Nó XSLT/literal a inserir.</param>
    /// <param name="Test">Teste do xsl:if (null = incondicional).</param>
    private sealed record Emissao(int Order, string ParentPath, XNode Conteudo, string? Test);

    private void MontarEscopo(XElement container, string basePath, List<Emissao> emissoes)
    {
        foreach (var e in emissoes.OrderBy(e => e.Order))
        {
            var parent = CriarCadeia(container, basePath, e.ParentPath);
            parent.Add(e.Test is null
                ? e.Conteudo
                : new XElement(Xs + "if", new XAttribute("test", e.Test), e.Conteudo));
        }
    }

    /// <summary>Navega/cria os grupos literais entre basePath e parentPath.</summary>
    private static XElement CriarCadeia(XElement container, string basePath, string parentPath)
    {
        var current = container;
        if (parentPath.Length <= basePath.Length) return current;
        var rel = parentPath[(basePath.Length + 1)..];
        foreach (var seg in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Elements()
                .FirstOrDefault(x => x.Name.Namespace == XNamespace.None && x.Name.LocalName == seg);
            if (next is null)
            {
                next = new XElement(seg);
                current.Add(next);
            }
            current = next;
        }
        return current;
    }

    /// <summary>Insere um nó entre os filhos do pai respeitando a ordem do XSD.</summary>
    private void InserirNaOrdem(XElement parent, XNode novo, int order, string parentPath)
    {
        foreach (var irmao in parent.Elements())
        {
            var el = irmao.Name.Namespace == XNamespace.None
                ? irmao
                : irmao.Elements().FirstOrDefault(x => x.Name.Namespace == XNamespace.None);
            if (el is null) continue;
            if (_xsd.TryByPath($"{parentPath}/{el.Name.LocalName}", out var n) && n.Order > order)
            {
                irmao.AddBeforeSelf(novo);
                return;
            }
        }
        parent.Add(novo);
    }

    /// <summary>
    /// Grupo opcional (minOccurs=0 ou variante de choice) em que TODOS os filhos
    /// são condicionais → embrulha no xsl:if com o OR dos testes (§7.3.4:
    /// opcional vazio é OMITIDO, inclusive o grupo inteiro).
    /// </summary>
    private void EmbrulharGruposOpcionais(XElement el, string path)
    {
        foreach (var child in el.Elements()
                     .Where(c => c.Name.Namespace == XNamespace.None).ToList())
        {
            var childPath = $"{path}/{child.Name.LocalName}";
            EmbrulharGruposOpcionais(child, childPath);

            if (!_xsd.TryByPath(childPath, out var node) || !node.IsGroup) continue;
            if (node.Occurs.StartsWith('1') && !node.InChoice) continue;

            var testes = new List<string>();
            var todosCondicionais = true;
            foreach (var c in child.Elements())
            {
                if (c.Name == Xs + "if") testes.Add((string)c.Attribute("test")!);
                else if (c.Name == Xs + "comment") { /* comentário não conta */ }
                else { todosCondicionais = false; break; }
            }
            if (!todosCondicionais || testes.Count == 0) continue;

            var wrapper = new XElement(Xs + "if",
                new XAttribute("test", string.Join(" or ", testes.Distinct())));
            child.ReplaceWith(wrapper);
            wrapper.Add(child);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Valor e teste por campo (regras §7.3)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Expressão XPath do VALOR formatado + teste de emissão do campo.
    /// Decimais por STRING (sem double); zeros à esquerda por tipo XSD;
    /// datas por máscara; default = normalize-space.
    /// </summary>
    private static (string Expr, string Test) ValorETeste(SpecField f, XsdLeiauteNode node, string sel)
    {
        var tam = f.Fim - f.Inicio + 1;

        // N com decimais: insere o ponto por substring (precisão exata em 10 casas).
        if (f.Tipo == 'N' && f.Decimais is int d and > 0 && tam > d)
        {
            var intLen = tam - d;
            var expr = $"concat(format-number(number(substring({sel},1,{intLen})),'0'),"
                     + $"'.',substring({sel},{intLen + 1},{d}))";
            var test = ZeroOmitTypes.Contains(node.TypeName)
                ? $"number({sel}) > 0"                     // quantidade zero = não informado
                : $"normalize-space({sel}) != ''";         // 0.00 monetário é emitido
            return (expr, test);
        }

        // Datas: AAAAMMDD → ISO; demais máscaras já vêm prontas do input.
        if (f.Tipo == 'D')
        {
            var mask = f.Formato?.Trim().ToUpperInvariant();
            var expr = mask == "AAAAMMDD"
                ? $"concat(substring({sel},1,4),'-',substring({sel},5,2),'-',substring({sel},7,2))"
                : $"normalize-space({sel})";
            return (expr, $"normalize-space({sel}) != ''");
        }

        // Tipos sem zeros à esquerda (serie '006'→'6', nNF '000150839'→'150839').
        if (StripTypes.Contains(node.TypeName))
            return ($"format-number(number({sel}),'0')", $"normalize-space({sel}) != ''");

        // N inteiro: códigos curtos (≤3: orig '0', tpAmb, EXTIPI '000') são emitidos
        // como vêm; campos longos zerados (qVol '000…0') significam "não informado".
        if (f.Tipo == 'N')
        {
            var test = tam > 3 ? $"number({sel}) > 0" : $"normalize-space({sel}) != ''";
            return ($"normalize-space({sel})", test);
        }

        return ($"normalize-space({sel})", $"normalize-space({sel}) != ''");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════

    private string SelTop(Uso u) => $"ROOT/{u.B.Name}/{u.Slug}";

    /// <summary>Select no escopo do item: linha-mestre relativa; irmãs por ordinal [$i].</summary>
    private string SelDet(Uso u, string masterDet) =>
        u.B.Name == masterDet ? u.Slug : $"../{u.B.Name}[$i]/{u.Slug}";

    private static string ParentOf(string path) => path[..path.LastIndexOf('/')];

    /// <summary>Desce por filhos literais pelo nome ("NFe", "infNFe"); null se faltar.</summary>
    private static XElement? Achar(XElement el, params string[] nomes)
    {
        var atual = el;
        foreach (var nome in nomes)
        {
            atual = atual?.Elements()
                .FirstOrDefault(x => x.Name.Namespace == XNamespace.None && x.Name.LocalName == nome);
            if (atual is null) return null;
        }
        return atual;
    }

    private static string LeafName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static XElement ValueOf(string expr) =>
        new(Xs + "value-of", new XAttribute("select", expr));

    private static string VersaoDoLeiaute(string sheetName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(sheetName, @"(\d+\.\d+)\s*$");
        return m.Success ? m.Groups[1].Value : "4.00";
    }

    /// <summary>Dobra diacríticos para comparação (reusa o mapa do TclGenerator via slug).</summary>
    private static string Fold(string s) =>
        string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                span[i] = src[i] switch
                {
                    'á' or 'à' or 'â' or 'ã' or 'ä' => 'a',
                    'é' or 'è' or 'ê' or 'ë' => 'e',
                    'í' or 'ì' or 'î' or 'ï' => 'i',
                    'ó' or 'ò' or 'ô' or 'õ' or 'ö' => 'o',
                    'ú' or 'ù' or 'û' or 'ü' => 'u',
                    'ç' => 'c',
                    var c => char.ToLowerInvariant(c)
                };
        });

    private void Nota(string n) => _notas.Add(n);
}
