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

    // Grupos especializados do produto (variantes por TIPO de produto — as posições
    // da spec se sobrepõem entre eles). Sem driver de variante → descarte (Etapa B).
    private static readonly System.Text.RegularExpressions.Regex EspecializadoProd = new(
        @"/prod/(veicProd|med|arma|comb|DI|detExport|rastro|nRECOPI|infProdNFF|infProdEmb|gCred|gAgropecuario)/",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly List<string> _notas = new();
    private int _descartados;

    private XsdLeiauteIndex _xsd = null!;
    private XDocument _rootTree = null!;
    private MapperEmissionGuide _guia = MapperEmissionGuide.Empty;

    public XslGenReport Generate(
        SpecModel spec,
        NfeLeiauteCatalog catalog,
        XsdLeiauteIndex xsd,
        NfeGabarito? gabarito,
        XDocument rootTree,
        Action<string>? log = null,
        MapperEmissionGuide? guia = null)
    {
        _xsd = xsd;
        _rootTree = rootTree;
        _guia = guia ?? MapperEmissionGuide.Empty;
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
        // da montagem; o atributo Id vem da chave de acesso (44 chars); e R4/T1:
        // tpEmis/cNF/cDV/procEmi derivados da MESMA chave (vencem usos normais).
        var infCpl = EmitirInfCpl(spec, folhas);
        if (infCpl is not null) topo.Add(infCpl);
        var id = EmitirAtributoId(spec, detBlocks);
        if (id is not null) topo.Add(id);
        topo.AddRange(EmitirDerivadosDaChave(spec, detBlocks, folhas));

        // Choices ESCALARES fora do det (dest/emit/transporta: CNPJ|CPF|idEstrangeiro):
        // agrupadas por pai e emitidas como UM xsl:choose (R1 — rodada 1 §7.6).
        var choicesEscalares = new Dictionary<string, List<(Uso U, XsdLeiauteNode N)>>(StringComparer.Ordinal);

        foreach (var u in folhas)
        {
            if (!_xsd.TryByPath(u.Path, out var node) || node.IsGroup)
            {
                Nota($"#{u.F.XmlRef}: destino '{u.Path}' não é folha do XSD — descartado.");
                continue;
            }
            if (node.IsAttribute)
            {
                // R3: atributo (det/@nItem, infNFe/@Id) tem regra própria — nunca vira elemento.
                Nota($"#{u.F.XmlRef}: destino '{u.Path}' é ATRIBUTO no XSD (coberto por regra própria) — descartado.");
                _descartados++;
                continue;
            }
            var scopeDet = u.Path.StartsWith(DetPath + "/", StringComparison.Ordinal);
            if (scopeDet && EspecializadoProd.IsMatch(u.Path))
            {
                // Grupos ESPECIALIZADOS do produto (veículo/medicamento/combustível/
                // DI/export…): variantes que COMPARTILHAM posições na spec — sem o
                // driver do tipo de produto viram ruído. Descarte honesto (Etapa B).
                Nota($"#{u.F.XmlRef}: grupo especializado de produto ('{u.Path}') sem driver de variante — descartado (Etapa B).");
                _descartados++;
                continue;
            }
            if (node.InChoice && !scopeDet)
            {
                var pai = ParentOf(u.Path);
                if (!choicesEscalares.TryGetValue(pai, out var lista))
                    choicesEscalares[pai] = lista = new List<(Uso, XsdLeiauteNode)>();
                lista.Add((u, node));
                continue;
            }
            var sel = scopeDet ? SelDet(u, masterDet) : SelTop(u);
            var (expr, test) = ValorETeste(u.F, node, sel, scopeDet);
            // Etapa B: guia do MAPEADOR — destino com guarda "!= 0" na regra DSL
            // real só emite com valor > 0 (retTrib, cobr/fat/vLiq…). Só APERTA o
            // teste; NaN (vazio) e zeros ficam de fora, como no gabarito.
            if (_guia.ExigeNaoZero(u.Path))
            {
                test = $"number(translate({sel},' ','')) > 0";
                Nota($"#{u.F.XmlRef}: guia do mapeador — '{LeafName(u.Path)}' só emite com valor != 0 (Etapa B).");
            }
            var conteudo = new XElement(node.Name, ValueOf(expr));
            (scopeDet ? det : topo).Add(new Emissao(node.Order, ParentOf(u.Path), conteudo, test));
        }

        topo.AddRange(EmitirChoicesEscalares(choicesEscalares));

        // Choices de imposto (dentro do escopo det).
        det.AddRange(EmitirChoices(membros, masterDet));

        // ── 7. Monta o stylesheet ─────────────────────────────────────────────
        var enviNFe = new XElement("enviNFe", new XAttribute("versao", versao));
        MontarEscopo(enviNFe, "enviNFe", topo);

        var infNFe = Achar(enviNFe, "NFe", "infNFe");
        if (infNFe is not null)
        {
            // B3 (cosmético): o gabarito serializa <infNFe Id="…" versao="…"> — Id ANTES
            // de versao. xsl:attribute sai sempre DEPOIS dos atributos literais do
            // elemento, então o Id vira atributo literal com AVT (mesma expressão,
            // avaliada em runtime) e os dois entram na ordem do gabarito — o XElement
            // serializa atributos na ordem de Add.
            var idAttr = infNFe.Elements(Xs + "attribute")
                .FirstOrDefault(a => (string?)a.Attribute("name") == "Id");
            var idSelect = idAttr?.Element(Xs + "value-of")?.Attribute("select")?.Value;
            if (idSelect is not null)
            {
                idAttr!.Remove();
                infNFe.Add(new XAttribute("Id", $"{{{idSelect}}}"));
            }
            infNFe.Add(new XAttribute("versao", versao));
        }

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

        // R1b: remove cascas (grupos literais que sobraram só com comentários de gap).
        RemoverCascasVazias(enviNFe);

        // Etapa B2: envelope (idLote/indSinc) + bloco proprietário <dadosAdic>.
        // DEPOIS do RemoverCascasVazias — o B2BDirectory é intencionalmente vazio.
        new DadosAdicEmitter().Aplicar(enviNFe, spec, _rootTree, log);

        var doc = new XDocument(
            new XElement(Xs + "stylesheet",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsl", Xs.NamespaceName),
                new XElement(Xs + "output",
                    new XAttribute("method", "xml"),
                    new XAttribute("encoding", "utf-8"),
                    new XAttribute("indent", "no")),
                new XElement(Xs + "template", new XAttribute("match", "/"), enviNFe),
                BuildTrimTemplate()));

        // Conta folhas emitidas por value-of OU pelo call-template de trim (R6).
        var emitidas = doc.Descendants(Xs + "value-of").Count()
            + doc.Descendants(Xs + "call-template")
                .Count(e => (string?)e.Attribute("name") == TrimTemplateName);
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
            else if (!u.DetBlock && candidatos.Count > 1
                     && MelhorPorAfinidade(candidatos, usos, u) is { } eleito)
            {
                // R2 (rodada 1 §7.6): homônimos de TOTAIS (vPIS em ICMSTot × ISSQNtot) →
                // vence o pai com mais destinos do MESMO bloco. SÓ para blocos não-det:
                // na região det a afinidade inunda prod/* com campos que o dedup por
                // valor então elege errado (regressão da rodada 2: qCom cru, SOBRA DI/comb).
                Nota($"#{u.F.XmlRef}: reancorado por afinidade de bloco ({u.Path} → {eleito.XPath}).");
                u.Path = eleito.XPath;
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
        // Inclui o CURINGA "IPI/*/CST" (multi-ref IPITrib|IPINT com docs idênticas
        // após o strip de enum — R4). Folhas DIRETAS do IPI (CNPJProd, cEnq) NÃO
        // são membros do choice e continuam como folhas normais.
        if (rel.StartsWith("IPI/IPITrib/", StringComparison.Ordinal)
            || rel.StartsWith("IPI/IPINT/", StringComparison.Ordinal)
            || rel.StartsWith("IPI/*/", StringComparison.Ordinal)) return Regiao.Ipi;
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
                var (expr, test) = ValorETeste(u.F, node, sel, scopeDet: true);
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
    ///
    /// R6 (IVECCO gap #5 — TENTATIVA REVERTIDA): tentamos afrouxar para
    /// bloco com >=1 ocorrência (IVECCO tem o 081 em UMA só, vazia, e o
    /// gabarito ainda emite &lt;infCpl&gt;&lt;/infCpl&gt;) mas isso fez o
    /// primeiro bloco "complementar"-like de OUTRA região (produto, com
    /// conteúdo real "Suspensao de IPI…") vencer por ordem — regressão pior
    /// que o gap original. Mantido o requisito de repetição (>1) até termos
    /// um sinal melhor para escolher o bloco 081 correto sem depender da
    /// contagem de ocorrências (ver nota para @lp-parser-llm no handoff).
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
                // Campos de CONTROLE carregam a descrição do bloco ("Bloco-081 -
                // Informações para EDI") — não são o conteúdo; pula.
                if (slug is "Tipo_Registro" or "codigoRegistro") continue;

                var desc = Fold(f.FieldName ?? "");
                // "Informações Complementares" OU a convenção FiatMQ "Informações
                // para EDI" (o bloco repetido 081 alimenta o infCpl no gabarito).
                if (!desc.Contains("complementar", StringComparison.OrdinalIgnoreCase)
                    && !desc.Contains("para edi", StringComparison.OrdinalIgnoreCase)) continue;

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
                // R6 (IVECCO gap #5): o mapeador emite <infCpl></infCpl> mesmo VAZIO
                // quando o bloco repetido existe mas todas as ocorrências estão em
                // branco (achado no gabarito IVECCO). O teste antigo exigia >=1
                // segmento não-vazio para emitir o elemento — bastava o BLOCO
                // existir (>=1 ocorrência), independente do conteúdo.
                var teste = $"ROOT/{block.Name}";
                Nota($"infCpl: concat dos segmentos de {block.Name}/{slug} (regra §7.3.5; "
                    + "elemento emitido mesmo vazio quando o bloco existe).");
                return new Emissao(node.Order, ParentOf(infCplPath), folha, teste);
            }
        }
        return null;
    }

    /// <summary>
    /// Localiza o campo da CHAVE DE ACESSO na spec: o único com Tamanho 44 e
    /// 'chave' na descrição, fora da região det. Null se não existir.
    /// </summary>
    private static (SpecBlock Block, string Slug)? AcharCampoChave(SpecModel spec, HashSet<string> detBlocks)
    {
        foreach (var block in spec.Blocks.Where(b => !detBlocks.Contains(b.Name)))
        {
            foreach (var (f, slug) in TclGenerator.NamedFields(block))
            {
                if (f.Tamanho != 44) continue;
                if (!Fold(f.FieldName ?? "").Contains("chave", StringComparison.OrdinalIgnoreCase)) continue;
                return (block, slug);
            }
        }
        return null;
    }

    /// <summary>
    /// Atributo obrigatório infNFe/@Id = 'NFe' + chave de acesso (44 dígitos).
    /// </summary>
    private Emissao? EmitirAtributoId(SpecModel spec, HashSet<string> detBlocks)
    {
        const string idPath = "enviNFe/NFe/infNFe/@Id";
        if (!_xsd.TryByPath(idPath, out var node)) return null;

        var chave = AcharCampoChave(spec, detBlocks);
        if (chave is null)
        {
            Nota("@Id: campo da chave de acesso (44 chars) NÃO encontrado — XSD acusará a falta.");
            return null;
        }
        var (block, slug) = chave.Value;

        var attr = new XElement(Xs + "attribute", new XAttribute("name", "Id"),
            new XElement(Xs + "value-of",
                new XAttribute("select", $"concat('NFe', normalize-space(ROOT/{block.Name}/{slug}))")));
        Nota($"@Id: NFe + {block.Name}/{slug} (campo de 44 chars com 'chave' na descrição).");
        return new Emissao(node.Order, InfNFePath, attr, Test: null);
    }

    // Leiaute FIXO da chave de acesso NF-e (44 dígitos), verificado no gabarito:
    //   cUF(1-2) AAMM(3-6) CNPJ(7-20) mod(21-22) serie(23-25) nNF(26-34)
    //   tpEmis(35) cNF(36-43) cDV(44)
    // Campos de ide deriváveis por substring (R4/T1): posição 1-based p/ XPath.
    private static readonly (string Leaf, int Inicio, int Tamanho)[] DerivadosDaChave =
    [
        ("tpEmis", 35, 1), ("cNF", 36, 8), ("cDV", 44, 1)
    ];

    /// <summary>
    /// R4/T1 — campos de ide derivados da CHAVE DE ACESSO: tpEmis/cNF/cDV por
    /// substring da chave (leiaute fixo acima) e procEmi = constante '0'
    /// (NF-e emitida por aplicativo do contribuinte — convenção do mapeador).
    /// O especial VENCE qualquer uso normal nesses destinos (dedup honesto,
    /// mesmo padrão do infCpl): o cDV vinha de um campo mal-resolvido e saía '0'.
    /// </summary>
    private IEnumerable<Emissao> EmitirDerivadosDaChave(
        SpecModel spec, HashSet<string> detBlocks, List<Uso> folhas)
    {
        const string idePath = "enviNFe/NFe/infNFe/ide";
        var campo = AcharCampoChave(spec, detBlocks);
        if (campo is null) yield break;
        var (block, slug) = campo.Value;

        var chave = $"normalize-space(ROOT/{block.Name}/{slug})";
        var destinos = DerivadosDaChave.Select(d => $"{idePath}/{d.Leaf}")
            .Append($"{idePath}/procEmi")
            .ToHashSet(StringComparer.Ordinal);

        // Dedup: o valor derivado da chave é AUTORITATIVO — remove usos normais.
        folhas.RemoveAll(u =>
        {
            var bate = destinos.Contains(u.Path);
            if (bate) Nota($"#{u.F.XmlRef}: '{u.Path}' coberto pela derivação da chave de acesso (R4/T1).");
            return bate;
        });

        foreach (var (leaf, inicio, tamanho) in DerivadosDaChave)
        {
            if (!_xsd.TryByPath($"{idePath}/{leaf}", out var node)) continue;
            var folha = new XElement(node.Name,
                ValueOf($"substring({chave},{inicio},{tamanho})"));
            Nota($"{leaf}: substring({block.Name}/{slug},{inicio},{tamanho}) — derivado da chave de acesso.");
            yield return new Emissao(node.Order, idePath, folha, Test: null);
        }

        if (_xsd.TryByPath($"{idePath}/procEmi", out var procEmi))
        {
            Nota("procEmi: constante '0' (emissão por aplicativo do contribuinte).");
            yield return new Emissao(procEmi.Order, idePath, new XElement("procEmi", "0"), Test: null);
        }
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
        foreach (var child in el.Elements().ToList())
        {
            // Desce por elementos XSL (for-each/if/choose…) SEM avançar o path
            // literal — senão os grupos do det (impostoDevol, DFeReferenciado…)
            // nunca são visitados e viram casca vazia (bug da rodada 1 §7.6/R1).
            if (child.Name.Namespace != XNamespace.None)
            {
                EmbrulharGruposOpcionais(child, path);
                continue;
            }

            var childPath = $"{path}/{child.Name.LocalName}";
            EmbrulharGruposOpcionais(child, childPath);

            if (!_xsd.TryByPath(childPath, out var node) || !node.IsGroup) continue;
            if (node.Occurs.StartsWith('1') && !node.InChoice) continue;

            // R4: coleta RECURSIVA — grupos literais intermediários obrigatórios
            // (ex.: impostoDevol→IPI 1-1) são atravessados; sem isso a casca
            // <impostoDevol><IPI/></impostoDevol> vazava quando o teste da única
            // folha (vIPIDevol zerado) não disparava em runtime.
            var testes = ColetarTestesSeTudoCondicional(child);
            if (testes is null || testes.Count == 0) continue;

            var wrapper = new XElement(Xs + "if",
                new XAttribute("test", string.Join(" or ", testes.Distinct())));
            child.ReplaceWith(wrapper);
            wrapper.Add(child);
        }
    }

    /// <summary>
    /// Testes de emissão de TODO o conteúdo de um grupo, descendo por grupos
    /// literais aninhados. Null = existe conteúdo INCONDICIONAL (value-of,
    /// for-each, texto) em algum nível — o grupo sempre terá conteúdo e não
    /// deve ser embrulhado.
    /// </summary>
    private static List<string>? ColetarTestesSeTudoCondicional(XElement grupo)
    {
        var testes = new List<string>();
        foreach (var n in grupo.Nodes())
        {
            switch (n)
            {
                case XComment:
                case XText t when string.IsNullOrWhiteSpace(t.Value):
                    continue;
                case XElement el when el.Name == Xs + "if":
                    testes.Add((string)el.Attribute("test")!);
                    continue;
                case XElement el when el.Name == Xs + "choose":
                    testes.AddRange(el.Elements(Xs + "when")
                        .Select(w => (string)w.Attribute("test")!));
                    continue;
                case XElement el when el.Name.Namespace == XNamespace.None:
                    var sub = ColetarTestesSeTudoCondicional(el);
                    if (sub is null) return null;
                    testes.AddRange(sub);
                    continue;
                default:
                    return null;   // value-of/for-each/texto = conteúdo incondicional
            }
        }
        return testes;
    }

    /// <summary>
    /// R1b: pós-passo que remove grupos literais SEM conteúdo real (sobraram só
    /// comentários de gap) — o gabarito nunca tem elemento vazio (§7.3.4).
    /// </summary>
    private static void RemoverCascasVazias(XElement el)
    {
        foreach (var child in el.Elements()
                     .Where(c => c.Name.Namespace == XNamespace.None).ToList())
        {
            RemoverCascasVazias(child);
            var temConteudo = child.Nodes().Any(n =>
                n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value)));
            if (!temConteudo && !child.HasAttributes) child.Remove();
        }
    }

    /// <summary>
    /// R1: choice de folhas ESCALARES (dest/emit/transporta CNPJ|CPF|idEstrangeiro):
    /// UM xsl:choose na ordem do XSD, escolhendo a 1ª alternativa com conteúdo REAL
    /// — zeros-only conta como vazio (idEstrangeiro '000…0' não é identificação).
    /// </summary>
    private IEnumerable<Emissao> EmitirChoicesEscalares(
        Dictionary<string, List<(Uso U, XsdLeiauteNode N)>> porPai)
    {
        foreach (var (pai, membros) in porPai)
        {
            var ordenados = membros.OrderBy(m => m.N.Order).ToList();
            var testes = new List<string>();
            var choose = new XElement(Xs + "choose");
            foreach (var (u, node) in ordenados)
            {
                var sel = SelTop(u);
                var (expr, _) = ValorETeste(u.F, node, sel);
                var teste = $"translate(normalize-space({sel}), '0', '') != ''";
                testes.Add(teste);
                choose.Add(new XElement(Xs + "when", new XAttribute("test", teste),
                    new XElement(node.Name, ValueOf(expr))));
            }

            if (ordenados.Count == 1)
            {
                // Sem irmão de choice resolvido: emite direto (teste zeros-aware).
                var (u, node) = ordenados[0];
                var (expr, _) = ValorETeste(u.F, node, SelTop(u));
                yield return new Emissao(node.Order, pai,
                    new XElement(node.Name, ValueOf(expr)), testes[0]);
                continue;
            }
            yield return new Emissao(ordenados[0].N.Order, pai, choose,
                string.Join(" or ", testes));
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
    private static (string Expr, string Test) ValorETeste(
        SpecField f, XsdLeiauteNode node, string sel, bool scopeDet = false)
    {
        var tam = f.Fim - f.Inicio + 1;

        // N com decimais: insere o ponto por substring (precisão exata em 10 casas).
        // R5: o corte é pela LARGURA REAL (strip de espaços via translate) — slices
        // com padding (ex.: '00000000000000 ') quebravam o corte fixo da spec.
        // Rodada 4: o nº de casas vem do TIPO XSD (TDec_0302a04 → 2; TDec_1104v → 4);
        // a coluna Decimais da spec (range '0-4') é só fallback.
        var casas = DecimaisDoTipo(node.TypeName) ?? f.Decimais;
        if (f.Tipo == 'N' && casas is int d and > 0 && tam > d)
        {
            var v = $"translate({sel},' ','')";
            var expr = $"concat(format-number(number(concat('0',substring({v},1,string-length({v})-{d}))),'0'),"
                     + $"'.',substring({v},string-length({v})-{d}+1,{d}))";
            // Convenção do gabarito: no ITEM (det) o decimal OPCIONAL zerado é
            // OMITIDO (prod/vFrete, vSeg…); nos totais/transp o 0.00 é emitido.
            // Zero-omit por PREFIXO de tipo (TDec_1204v ≠ match exato — rodada 4).
            var zeroOmit = ZeroOmitTypes.Any(t => node.TypeName.StartsWith(t, StringComparison.Ordinal));
            var test = zeroOmit || (scopeDet && node.Occurs.StartsWith('0'))
                ? $"number({sel}) > 0"                     // zero = não informado
                : $"normalize-space({sel}) != ''";         // 0.00 é emitido
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
            // R6 (IVECCO gap #3): a heurística "código longo tudo-zero = não
            // informado" (qVol, EXTIPI…) NÃO vale para "fone" — o gabarito emite
            // '00000000000000' verbatim (é um placeholder, não uma quantidade
            // omissível). XSD tipa os dois como xs:string+pattern numérico
            // idêntico, então a distinção é por NOME, não por tipo.
            var test = tam > 3 && node.Name != "fone"
                ? $"number({sel}) > 0" : $"normalize-space({sel}) != ''";
            return ($"normalize-space({sel})", test);
        }

        // R6 (IVECCO gap #1): campo texto GENÉRICO — normalize-space() colapsa
        // espaço INTERNO duplo que alguns gabaritos preservam (ex.: xProd com
        // "ABR60  91752882", dois espaços reais na origem). O valor emitido usa
        // o template TrimPreservaInterno (só apara as pontas, via marcador
        // TrimMarker interpretado por ValueOf); o TESTE de emissão continua em
        // normalize-space (não importa espaço interno para decidir se emite).
        return ($"{TrimMarker}{sel}", $"normalize-space({sel}) != ''");
    }

    // Marcador consumido por ValueOf: em vez de <xsl:value-of select="{sel}"/>,
    // gera um <xsl:call-template name="TrimPreservaInterno"> que apara só as
    // pontas do valor, preservando espaços internos (normalize-space colapsa
    // os dois, o que quebra campos como xProd com espaço duplo REAL no dado).
    private const string TrimMarker = "TRIM";

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

    /// <summary>
    /// Casas decimais implícitas no TIPO XSD do leiaute: TDec_iiDD[aMM][v] → DD
    /// (base, não o máximo): TDec_0302a04→2, TDec_1104v→4, TDec_1110→10. null se
    /// o tipo não for TDec (aí vale a coluna Decimais da spec).
    /// </summary>
    private static int? DecimaisDoTipo(string typeName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(typeName, @"^TDec_\d{2}(\d{2})");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// R2: desempate entre folhas homônimas de mesma região pela AFINIDADE do
    /// bloco — vence o pai que já hospeda mais destinos resolvidos de campos do
    /// MESMO bloco da spec (empate ou zero → null = descarte honesto).
    /// </summary>
    private static XsdLeiauteNode? MelhorPorAfinidade(
        List<XsdLeiauteNode> candidatos, List<Uso> usos, Uso u)
    {
        var placar = candidatos
            .Select(c => (Node: c, Afinidade: usos.Count(o =>
                !ReferenceEquals(o, u) && o.B.Name == u.B.Name
                && o.Path.StartsWith(ParentOf(c.XPath) + "/", StringComparison.Ordinal))))
            .OrderByDescending(p => p.Afinidade)
            .ToList();
        return placar[0].Afinidade > 0 && placar[0].Afinidade > placar[1].Afinidade
            ? placar[0].Node : null;
    }

    private static XElement ValueOf(string expr)
    {
        if (expr.StartsWith(TrimMarker, StringComparison.Ordinal))
        {
            var sel = expr[TrimMarker.Length..];
            return new XElement(Xs + "call-template", new XAttribute("name", TrimTemplateName),
                new XElement(Xs + "with-param", new XAttribute("name", "s"), new XAttribute("select", sel)));
        }
        return new(Xs + "value-of", new XAttribute("select", expr));
    }

    private const string TrimTemplateName = "TrimPreservaInterno";

    /// <summary>
    /// Template recursivo XSLT 1.0 que apara SÓ as pontas (espaço à esquerda e
    /// à direita), preservando espaço INTERNO — normalize-space() nativo colapsa
    /// os dois, o que quebra campos texto com espaço duplo real (R6, gap #1
    /// diagnosticado em IVECCO: xProd = "…ABR60  91752882…").
    /// </summary>
    private static XElement BuildTrimTemplate() =>
        new(Xs + "template", new XAttribute("name", TrimTemplateName),
            new XElement(Xs + "param", new XAttribute("name", "s")),
            new XElement(Xs + "choose",
                new XElement(Xs + "when", new XAttribute("test", "starts-with($s,' ')"),
                    new XElement(Xs + "call-template", new XAttribute("name", TrimTemplateName),
                        new XElement(Xs + "with-param", new XAttribute("name", "s"),
                            new XAttribute("select", "substring($s,2)")))),
                new XElement(Xs + "when",
                    new XAttribute("test", "$s != '' and substring($s,string-length($s))=' '"),
                    new XElement(Xs + "call-template", new XAttribute("name", TrimTemplateName),
                        new XElement(Xs + "with-param", new XAttribute("name", "s"),
                            new XAttribute("select", "substring($s,1,string-length($s)-1)")))),
                new XElement(Xs + "otherwise", new XElement(Xs + "value-of", new XAttribute("select", "$s")))));

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
