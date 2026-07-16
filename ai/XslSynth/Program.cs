using System.Xml.Linq;
using XslSynth.Core;
using XslSynth.Excel;
using XslSynth.Model;
using XslSynth.NtPipeline;
using XslSynth.Synthesis;

// ─────────────────────────────────────────────────────────────────────────────
// XslSynth — loop de síntese de XSLT guiada por verificador.
//
//   dotnet run                 → MAPEADOR REAL, tradução OFFLINE (fallback determinístico)
//   dotnet run -- --ollama     → MAPEADOR REAL, regras traduzidas pelo LLM local (Ollama)
//   dotnet run -- <mapper.xml> → usa um MapperVO .xml específico (descriptografado)
//   dotnet run -- --sample     → exemplo sintético + loop de reparo diff==0 (MVP Fase 0-2)
//   dotnet run -- --excel <x>  → gera o TCL <MAP> a partir da spec .xlsx (PoC-0/1)
//   dotnet run -- --catalog <x> → catálogo #XML → XPath NF-e + relatório (PoC-2)
//   dotnet run -- --generate <x> → TXT→ROOT→XSL→saída + diff vs gabarito (PoC-3)
//   dotnet run -- --rag <pasta>  → injeta o índice RAG few-shot no tradutor (P1)
//   dotnet run -- --rag-stats    → tamanho do índice por chave + demo de recuperação
//   dotnet run -- --xsd-diff <velho.xsd> <novo.xsd> [--delta-out <json>]
//                              → S1 do pipeline NT: delta de XSD por XPath (B5 P-1)
//
// Arquitetura: docs/architecture/ia-xslt-synthesis.md · poc-excel-generator.md
// ─────────────────────────────────────────────────────────────────────────────

void Log(string line) => Console.WriteLine(line);

Log("╔══════════════════════════════════════════════════════════════════╗");
Log("║  XslSynth — síntese de XSLT guiada por verificador                 ║");
Log("╚══════════════════════════════════════════════════════════════════╝");
Log("");

if (args.Contains("--xsd-diff"))
    return RunXsdDiff();

if (args.Contains("--rag-stats"))
    return await RunRagStatsAsync();

if (args.Contains("--generate"))
    return RunGenerate();

if (args.Contains("--catalog"))
    return RunCatalog();

if (args.Contains("--excel"))
    return RunExcel();

if (args.Contains("--sample"))
    return await RunSampleAsync();

return await RunRealAsync();

// ══════════════════════════════════════════════════════════════════════════
// Fluxo EXCEL: spec .xlsx (fonte-da-verdade do layout) → TCL <MAP> determinístico
// (PoC-0 ExcelSpecParser + PoC-1 TclGenerator). Sem catálogo nem LLM.
// ══════════════════════════════════════════════════════════════════════════
int RunExcel()
{
    var xlsx = FindExcelArg();
    if (xlsx is null || !File.Exists(xlsx))
    {
        Log("❌ Planilha .xlsx não encontrada.");
        Log("   Uso: dotnet run -- --excel <caminho.xlsx>");
        return 2;
    }

    Log("Modo      : GERADOR via EXCEL (spec → TCL)");
    Log($"Planilha  : {xlsx}");
    Log("");

    SpecModel spec;
    try
    {
        spec = new ExcelSpecParser(Log).Parse(xlsx);
    }
    catch (Exception ex)
    {
        Log($"❌ Falha ao ler a spec: {ex.Message}");
        return 1;
    }

    // ── Passo 1: resumo do SpecModel ──────────────────────────────────────
    var totalFields = spec.Blocks.Sum(b => b.Fields.Count);
    var xmlFields = spec.Blocks.Sum(b => b.Fields.Count(f => f.XmlRef is not null));
    Log($"[1] SpecModel (aba '{spec.SheetName}'):");
    Log($"      Blocos (→ LINE)          : {spec.Blocks.Count}");
    Log($"      Campos totais (→ FIELD)  : {totalFields}");
    Log($"      Campos com #XML != NA    : {xmlFields}");
    Log("");
    Log("      Por bloco (name · nCampos · somaTam · maxFim · #XML!=NA):");
    foreach (var b in spec.Blocks)
    {
        var soma = b.Fields.Sum(f => f.Tamanho);
        var maxFim = b.Fields.Count > 0 ? b.Fields.Max(f => f.Fim) : 0;
        var xn = b.Fields.Count(f => f.XmlRef is not null);
        // Linha "saudável" cobre ~600 chars; desvio = grupos repetidos (posições sobrepostas).
        var flag = soma is >= 590 and <= 601 ? "" : "  <= soma≠~600 (grupos repetidos/overlap)";
        Log($"        {b.Name,-9} nF={b.Fields.Count,3}  somaTam={soma,5}  maxFim={maxFim,3}  #XML={xn,3}{flag}");
    }
    Log("");

    // ── Passo 2: gera o TCL <MAP> e grava em .claude/tmp/export/generated.tcl ──
    var tcl = new TclGenerator().Generate(spec);
    var outDir = ResolveExportDir();
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, "generated.tcl");
    tcl.Save(outPath);

    var lineCount = tcl.Root!.Elements("LINE").Count();
    var fieldCount = tcl.Root!.Elements("LINE").Sum(l => l.Elements("FIELD").Count());
    Log($"[2] TCL <MAP> gerado: {lineCount} LINE, {fieldCount} FIELD (XML bem-formado).");
    Log($"      Arquivo: {outPath}");
    Log("");

    Log("── Limite honesto (o que fica para a Lia / PoC-2 e 3) ────────────");
    Log("   • #XML → XPath NF-e ainda NÃO resolvido (catálogo é PoC-2).");
    Log("   • blocos com grupos repetidos (posições sobrepostas) hoje são");
    Log("     transcritos como FIELDs planos — precisam de construto aninhado.");
    Log("   • o XslGenerator (ROOT → NF-e) é PoC-3.");

    return 0;
}

// ══════════════════════════════════════════════════════════════════════════
// Fluxo CATALOG (PoC-2): spec .xlsx + XSD leiaute + gabarito real (opcional)
// → NfeLeiauteCatalog (#XML → XPath NF-e) por triangulação DETERMINÍSTICA
// (ordem no XSD + semântica das xs:documentation + ancoragem por valor).
// SEM LLM — 100% offline. Desenho: poc-excel-generator.md §3.4.
// ══════════════════════════════════════════════════════════════════════════
int RunCatalog()
{
    var xlsx = FindArgAfter("--catalog") ?? FindExcelArg();
    if (xlsx is null || !File.Exists(xlsx))
    {
        Log("❌ Planilha .xlsx não encontrada.");
        Log("   Uso: dotnet run -- --catalog <caminho.xlsx> [--xsd <leiaute.xsd>] [--txt <gabarito.txt>] [--xml <gabarito.xml>]");
        return 2;
    }

    // Defaults dos insumos: sob <raiz>/.claude/tmp (ResolveExportDir → …/.claude/tmp/export).
    var claudeTmp = Path.GetDirectoryName(ResolveExportDir())!;   // …/.claude/tmp
    var xsdPath = FindArgAfter("--xsd") ?? Path.Combine(claudeTmp, "servidor", "layoutparser",
        "xsd", "PL_010b_NT2025_002_v1.30", "leiauteNFe_v4.00.xsd");
    var txtPath = FindArgAfter("--txt") ?? Path.Combine(claudeTmp, "exemplos", "txt input",
        "QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series.txt");
    var xmlPath = FindArgAfter("--xml") ?? Path.Combine(claudeTmp, "exemplos", "xml output",
        "QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series-11072026094950273-env.xml");

    Log("Modo      : CATÁLOGO #XML → XPath NF-e (PoC-2, determinístico, sem LLM)");
    Log($"Planilha  : {xlsx}");
    Log($"XSD       : {xsdPath}");
    Log("");

    SpecModel spec;
    XsdLeiauteIndex xsd;
    try
    {
        spec = new ExcelSpecParser(Log).Parse(xlsx);
        xsd = XsdLeiauteIndex.Load(xsdPath);
    }
    catch (Exception ex)
    {
        Log($"❌ Falha ao carregar insumos: {ex.Message}");
        return 1;
    }

    // Gabarito é opcional: sem ele o catálogo degrada para 2 camadas (nunca derruba).
    NfeGabarito? gabarito = null;
    if (File.Exists(txtPath) && File.Exists(xmlPath))
    {
        try
        {
            gabarito = NfeGabarito.Load(txtPath, xmlPath);
            Log($"Gabarito  : {Path.GetFileName(txtPath)} + {Path.GetFileName(xmlPath)} ✅");
        }
        catch (Exception ex)
        {
            Log($"   [aviso] gabarito ilegível ({ex.Message}) — seguindo só com ordem+semântica.");
        }
    }
    else
    {
        Log("   [aviso] par gabarito não encontrado — seguindo só com ordem+semântica.");
    }

    var totalFields = spec.Blocks.Sum(b => b.Fields.Count(f => f.XmlRef is not null));
    Log($"XSD index : {xsd.Nodes.Count} nós em ordem de documento.");
    Log($"Spec      : {spec.Blocks.Count} blocos, {totalFields} campos com #XML != NA.");
    Log("");

    var catalog = NfeLeiauteCatalog.Build(spec, xsd, gabarito, Log, FindArgAfter("--debug-ref"));
    var res = catalog.Resolutions;

    // ── Validação das âncoras do arquiteto (quality gate: as 9 têm que bater) ──
    Log("── Validação das âncoras empíricas (arquiteto) ───────────────────");
    (string Ref, string Fim)[] esperadas =
    [
        ("6", "ide/cUF"), ("8", "ide/natOp"), ("10", "ide/mod"), ("11", "ide/serie"),
        ("12", "ide/nNF"), ("13", "ide/dhEmi"), ("14", "ide/dhSaiEnt"), ("15", "ide/tpNF"),
        ("16", "ide/cMunFG")
    ];
    var ancorasOk = 0;
    foreach (var (r, fim) in esperadas)
    {
        var ok = catalog.TryResolve(r, out var e) && e.XPath.EndsWith(fim, StringComparison.Ordinal);
        if (ok) ancorasOk++;
        Log($"   #{r,-3} → {fim,-14} {(ok ? "✅" : "❌ FALHOU")}");
    }
    Log("");

    // ── Cobertura por categoria × confiança ──────────────────────────────
    Log("── Cobertura (refs distintos normalizados) ───────────────────────");
    Log($"   {"Categoria",-14} {"total",5} {"Alta",5} {"Média",5} {"Baixa",5} {"NÃO",5}");
    foreach (var g in res.GroupBy(r => r.Categoria).OrderBy(g => g.Key))
    {
        Log($"   {g.Key,-14} {g.Count(),5} {g.Count(r => r.Confianca == NivelConfianca.Alta),5} "
            + $"{g.Count(r => r.Confianca == NivelConfianca.Media),5} "
            + $"{g.Count(r => r.Confianca == NivelConfianca.Baixa),5} "
            + $"{g.Count(r => r.Confianca == NivelConfianca.NaoResolvido),5}");
    }
    var resolvidos = res.Where(r => r.XPath is not null).ToList();
    Log($"   {"TOTAL",-14} {res.Count,5} {res.Count(r => r.Confianca == NivelConfianca.Alta),5} "
        + $"{res.Count(r => r.Confianca == NivelConfianca.Media),5} "
        + $"{res.Count(r => r.Confianca == NivelConfianca.Baixa),5} "
        + $"{res.Count(r => r.XPath is null),5}");
    Log("");

    // ── Cobertura em CAMPOS da planilha (os 712) ─────────────────────────
    var camposResolvidos = res.Where(r => r.XPath is not null).Sum(r => r.CamposNaSpec);
    var camposForaXsd = res.Where(r => r.XPath is not null && r.ForaDoXsd).Sum(r => r.CamposNaSpec);
    Log($"── Cobertura em CAMPOS da spec: {camposResolvidos}/{totalFields} "
        + $"({100.0 * camposResolvidos / totalFields:F1}%) — {camposForaXsd} em extensão dadosAdic ──");
    foreach (var s in new[] { SinalResolucao.ValueAnchor, SinalResolucao.Semantic, SinalResolucao.XsdOrder })
    {
        var campos = res.Where(r => r.XPath is not null && r.Sinais.HasFlag(s)).Sum(r => r.CamposNaSpec);
        Log($"   com sinal {s,-12}: {campos} campos");
    }
    Log("");

    // ── Spot-check: 10 resoluções de ALTA confiança ──────────────────────
    Log("── Spot-check: 10 resoluções de ALTA confiança ───────────────────");
    foreach (var r in resolvidos.Where(r => r.Confianca == NivelConfianca.Alta)
                 .OrderByDescending(r => r.CamposNaSpec).Take(10))
    {
        Log($"   #{r.XmlRef,-8} [{r.Sinais}] {Short(r.Descricao, 38)}");
        Log($"      → {r.XPath}");
    }
    Log("");

    // ── Gaps honestos: NÃO resolvidos, agrupados ─────────────────────────
    var nao = res.Where(r => r.XPath is null).ToList();
    Log($"── NÃO resolvidos ({nao.Count} refs distintos) — por categoria ──────");
    foreach (var g in nao.GroupBy(r => r.Categoria).OrderByDescending(g => g.Count()))
    {
        Log($"   {g.Key} ({g.Count()}):");
        foreach (var r in g.Take(12))
            Log($"      #{Short(r.XmlRef, 22),-22} {Short(r.Descricao, 44)}{(r.Observacao is null ? "" : $" [{Short(r.Observacao, 40)}]")}");
        if (g.Count() > 12) Log($"      … e mais {g.Count() - 12}.");
    }
    Log("");

    // ── Artefato CSV para a PoC-3 (XslGenerator) ─────────────────────────
    var outDir = ResolveExportDir();
    Directory.CreateDirectory(outDir);
    var csvPath = Path.Combine(outDir, "nfe-leiaute-catalog.csv");
    using (var wtr = new StreamWriter(csvPath))
    {
        wtr.WriteLine("xmlRef;categoria;xpath;tipo;occurs;sinais;confianca;foraDoXsd;campos;descricao;observacao");
        foreach (var r in res)
        {
            wtr.WriteLine(string.Join(';',
                Csv(r.XmlRef), r.Categoria, Csv(r.XPath ?? ""), Csv(r.Tipo), Csv(r.Occurs),
                r.Sinais, r.Confianca, r.ForaDoXsd, r.CamposNaSpec,
                Csv(r.Descricao), Csv(r.Observacao ?? "")));
        }
    }
    Log($"Catálogo completo salvo em: {csvPath}");
    Log("");

    Log("── Limite honesto (o que fica para a PoC-3 / XslGenerator) ───────");
    Log("   • multi-refs (choice ICMS) resolvem para XPath curinga — a geração");
    Log("     precisa escolher a VARIANTE pelo CST em runtime.");
    Log("   • refs de extensão (FIAT/ANFAVEA/dadosAdic) não são leiaute SEFAZ.");
    Log("   • confiança Média = 1 sinal — vale revisar amostra antes de gerar.");

    return ancorasOk == esperadas.Length ? 0 : 1;
}

// ══════════════════════════════════════════════════════════════════════════
// Fluxo GENERATE (PoC-3): TXT real → ROOT (A1) → XSL gerado (A2) → saída →
// diff canônico do <infNFe> vs gabarito (A3, gate diff==0) + XSD como oráculo.
// 100% determinístico — SEM LLM. Desenho: poc-excel-generator.md §7.
// ══════════════════════════════════════════════════════════════════════════
int RunGenerate()
{
    var xlsx = FindArgAfter("--generate") ?? FindExcelArg();
    if (xlsx is null || !File.Exists(xlsx))
    {
        Log("❌ Planilha .xlsx não encontrada.");
        Log("   Uso: dotnet run -- --generate <caminho.xlsx> [--xsd <leiaute.xsd>] [--txt <input.txt>] [--xml <gabarito.xml>]");
        return 2;
    }

    var claudeTmp = Path.GetDirectoryName(ResolveExportDir())!;   // …/.claude/tmp
    var xsdDir = Path.Combine(claudeTmp, "servidor", "layoutparser", "xsd", "PL_010b_NT2025_002_v1.30");
    var xsdPath = FindArgAfter("--xsd") ?? Path.Combine(xsdDir, "leiauteNFe_v4.00.xsd");
    var txtPath = FindArgAfter("--txt") ?? Path.Combine(claudeTmp, "exemplos", "txt input",
        "QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series.txt");
    var xmlPath = FindArgAfter("--xml") ?? Path.Combine(claudeTmp, "exemplos", "xml output",
        "QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series-11072026094950273-env.xml");

    Log("Modo      : GERADOR XSL (PoC-3, determinístico, sem LLM)");
    Log($"Planilha  : {xlsx}");
    Log($"XSD       : {xsdPath}");
    Log($"TXT       : {txtPath}");
    Log($"Gabarito  : {xmlPath}");
    Log("");

    SpecModel spec;
    XsdLeiauteIndex xsd;
    NfeGabarito? gabarito = null;
    try
    {
        spec = new ExcelSpecParser(Log).Parse(xlsx);
        xsd = XsdLeiauteIndex.Load(xsdPath);
        if (File.Exists(txtPath) && File.Exists(xmlPath))
            gabarito = NfeGabarito.Load(txtPath, xmlPath);
        else
            Log("   [aviso] par gabarito incompleto — sem ancoragem por valor nem diff (A3).");
    }
    catch (Exception ex)
    {
        Log($"❌ Falha ao carregar insumos: {ex.Message}");
        return 1;
    }

    var outDir = ResolveExportDir();
    Directory.CreateDirectory(outDir);

    // ── A1: RootTreeBuilder (TXT → ROOT) + gate ──────────────────────────
    var rootReport = new RootTreeBuilder().Build(txtPath, spec, Log);
    var rootPath = Path.Combine(outDir, "generated-root.xml");
    rootReport.Root.Save(rootPath);

    var ocorrencias = rootReport.Root.Root!.Elements().Count();
    Log($"[A1] ROOT montado: {rootReport.Registros} registros de 600 chars → {ocorrencias} linhas "
        + $"({rootReport.RegistrosSemBloco} sem bloco na spec).");
    Log($"     Arquivo: {rootPath}");

    // Spot-checks do arquiteto, reproduzidos programaticamente.
    (string Bloco, int Ini, int Fim, string Esperado)[] spots =
    [
        ("LINHA001", 10, 11, "31"),
        ("LINHA001", 78, 86, "000150839"),
        ("LINHA004", 10, 23, "36519422000115"),
        ("LINHA005", 10, 23, "01844555002045")
    ];
    var spotsOk = 0;
    foreach (var (bloco, ini, fim, esperado) in spots)
    {
        var ok = SpotCheck(spec, rootReport.Root, bloco, ini, fim, esperado, out var obtido);
        if (ok) spotsOk++;
        Log($"     {bloco}[{ini}-{fim}] esperado='{esperado}' obtido='{obtido}' {(ok ? "✅" : "❌")}");
    }
    var a1Ok = ocorrencias == 59 && spotsOk == spots.Length;
    Log($"     Gate A1: {ocorrencias} ocorrências (esperado 59) + spot-check {spotsOk}/{spots.Length} "
        + $"→ {(a1Ok ? "PASSOU ✅" : "FALHOU ❌")}");
    Log("");

    // ── A2: catálogo + XslGenerator + compilação + XSD ───────────────────
    // Etapa B: guia de emissão do MAPEADOR real (guardas "!= 0" das regras DSL
    // que a spec/XSD não expressam — retTrib, vLiq). Degrade: sem o arquivo, vazio.
    var guia = MapperEmissionGuide.Empty;
    var mapperVoPath = Path.Combine(claudeTmp, "export", "MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.decrypted.xml");
    if (File.Exists(mapperVoPath))
    {
        try
        {
            guia = MapperEmissionGuide.Load(mapperVoPath);
            Log($"Guia mapeador: {guia.PathCount} destinos com guarda != 0 (Etapa B) ✅");
        }
        catch (Exception ex)
        {
            Log($"   [aviso] guia do mapeador ilegível ({ex.Message}) — seguindo sem.");
        }
    }
    else
    {
        Log("   [aviso] MapperVO descriptografado não encontrado — guia da Etapa B vazio.");
    }

    var catalog = NfeLeiauteCatalog.Build(spec, xsd, gabarito, Log);
    var gen = new XslGenerator().Generate(spec, catalog, xsd, gabarito, rootReport.Root, Log, guia);
    var xslPath = Path.Combine(outDir, "generated.xsl");
    gen.Xsl.Save(xslPath);
    // Diagnóstico completo: TODAS as notas (o console mostra só as 30 primeiras).
    File.WriteAllLines(Path.Combine(outDir, "generated-notes.txt"), gen.Notas);
    Log($"[A2] XSL gerado: {gen.FolhasEmitidas} folhas emitidas de {gen.CamposComRef} campos com #XML "
        + $"({gen.CamposDescartados} descartados; {gen.Notas.Count} notas → generated-notes.txt).");
    Log($"     Arquivo: {xslPath}");

    string saida;
    try
    {
        // B3: modo fiel ao gabarito — declaração <?xml version="1.0"?> + linha única,
        // como o mapeador de produção serializa (fecha os diffs cosméticos residuais).
        saida = new XsltApplier().Apply(gen.Xsl, rootReport.Root, fielAoGabarito: true);
    }
    catch (Exception ex)
    {
        Log($"     ❌ XSL NÃO COMPILOU/aplicou: {ex.Message}");
        return 1;
    }
    var outPath = Path.Combine(outDir, "generated-output.xml");
    File.WriteAllText(outPath, saida);
    Log($"     XSL compila e aplica ✅ — saída: {outPath}");

    // Oráculo XSD: valida o elemento <NFe> com o namespace SEFAZ injetado
    // (o pipeline trabalha sem namespace, como o gabarito de produção).
    var nfeXsd = Path.Combine(Path.GetDirectoryName(xsdPath)!, "nfe_v4.00.xsd");
    var saidaDoc = XDocument.Parse(saida);
    var nfeEl = saidaDoc.Descendants("NFe").FirstOrDefault();
    if (nfeEl is not null && File.Exists(nfeXsd))
    {
        XNamespace ns = "http://www.portalfiscal.inf.br/nfe";
        var comNs = ComNamespace(nfeEl, ns);
        var res = new XsdValidator().Validate(comNs.ToString(), nfeXsd);
        var deAssinatura = res.Errors.Count(e => e.Contains("Signature", StringComparison.Ordinal));
        var reais = res.Errors.Count - deAssinatura;
        Log($"     XSD (elemento NFe): {(reais == 0 ? "válido ✅" : $"{reais} erro(s) ❌")}"
            + (deAssinatura > 0 ? $" + {deAssinatura} de assinatura (esperado: PoC não assina)" : ""));
        foreach (var e in res.Errors.Where(e => !e.Contains("Signature", StringComparison.Ordinal)).Take(12))
            Log($"        {e}");
    }
    else
    {
        Log("     [aviso] validação XSD pulada (NFe ausente na saída ou nfe_v4.00.xsd não achado).");
    }
    Log("");

    // ── A3: diff canônico do <infNFe> vs gabarito ────────────────────────
    if (gabarito is null || !File.Exists(xmlPath))
    {
        Log("[A3] sem gabarito — diff não executado.");
        return a1Ok ? 0 : 1;
    }

    var esperadoInf = XDocument.Load(xmlPath).Descendants("infNFe").FirstOrDefault();
    var obtidoInf = saidaDoc.Descendants("infNFe").FirstOrDefault();
    if (esperadoInf is null || obtidoInf is null)
    {
        Log($"[A3] ❌ <infNFe> ausente ({(esperadoInf is null ? "gabarito" : "saída gerada")}).");
        return 1;
    }

    // ── Set-diff por PATH (gate honesto do R4): o differ posicional infla por
    // CASCATA (um elemento ausente desloca todos os irmãos e cada um vira
    // [NOME]). Aqui compara-se MULTICONJUNTOS de folhas/atributos por caminho
    // completo: FALTA/SOBRA por contagem de ocorrências, TEXTO por par ordinal.
    var (falta, sobra, texto, linhas) = SetDiffPorPath(esperadoInf, obtidoInf);
    Log($"[A3] set-diff por path <infNFe>: FALTA={falta}, SOBRA={sobra}, TEXTO={texto}.");
    foreach (var l in linhas) Log($"     {l}");
    Log("");

    // Diff posicional mantido como DETALHE (ordem dos irmãos ainda importa no gate final).
    var diffs = new CanonicalDiffer().Diff(esperadoInf.ToString(), obtidoInf.ToString());
    Log($"     (detalhe) diff posicional canônico: {diffs.Count} divergência(s).");
    foreach (var g in diffs.GroupBy(d => Regiao(d.XPath)).OrderByDescending(g => g.Count()))
    {
        Log($"     ── {g.Key} ({g.Count()}):");
        foreach (var d in g.Take(6)) Log($"        {d}");
        if (g.Count() > 6) Log($"        … e mais {g.Count() - 6}.");
    }
    Log("");
    Log(falta == 0 && texto == 0
        ? $"✅ GATE R4 ATINGIDO: FALTA=0 e TEXTO=0 no set-diff (SOBRA={sobra} → Etapa B, máscara do mapeador)."
        : $"❌ Gate R4 pendente: FALTA={falta}, TEXTO={texto} (ver listagem acima).");

    return a1Ok && falta == 0 && texto == 0 ? 0 : 1;
}

// ══════════════════════════════════════════════════════════════════════════
// Fluxo XSD-DIFF (pipeline NT, S1 — protótipo B5 P-1): snapshot dos dois
// pacotes XSD (includes resolvidos a partir da pasta de CADA arquivo) → delta
// por XPath (Added/Removed/TypeChanged/OccurrenceChanged/FacetChanged) →
// JSON versionável + resumo no console. 100% determinístico — SEM LLM.
// Desenho: docs/architecture/nt-pipeline-design.md §4–5.
// ══════════════════════════════════════════════════════════════════════════
int RunXsdDiff()
{
    var idx = Array.IndexOf(args, "--xsd-diff");
    string? Pos(int off) =>
        idx + off < args.Length && !args[idx + off].StartsWith("--") ? args[idx + off] : null;
    var velhoPath = Pos(1);
    var novoPath = Pos(2);
    if (velhoPath is null || novoPath is null || !File.Exists(velhoPath) || !File.Exists(novoPath))
    {
        Log("❌ Informe os dois XSD (arquivos existentes).");
        Log("   Uso: dotnet run -- --xsd-diff <velho.xsd> <novo.xsd> [--delta-out <arquivo.json>]");
        return 2;
    }

    Log("Modo      : XSD-DIFF (pipeline NT, S1 — determinístico, sem LLM)");
    Log($"XSD velho : {velhoPath}");
    Log($"XSD novo  : {novoPath}");
    Log("");

    XsdSchemaSnapshot velho, novo;
    try
    {
        velho = XsdDiffer.LoadSnapshot(velhoPath);
        novo = XsdDiffer.LoadSnapshot(novoPath);
    }
    catch (Exception ex)
    {
        Log($"❌ Falha ao compilar os XSD: {ex.Message}");
        return 1;
    }

    foreach (var aviso in velho.Avisos.Concat(novo.Avisos).Take(5))
        Log($"   [aviso XSD] {aviso}");

    Log($"Snapshot velho: {velho.Nodes.Count} nós ({velho.DuplicadosIgnorados} XPaths de choice colapsados).");
    Log($"Snapshot novo : {novo.Nodes.Count} nós ({novo.DuplicadosIgnorados} colapsados).");
    Log("");

    var delta = XsdDiffer.Diff(velho, novo);

    Log("── Resumo do delta ───────────────────────────────────────────────");
    foreach (var (kind, qtd) in delta.Resumo)
        Log($"   {kind,-18} {qtd,5}");
    Log($"   {"TOTAL",-18} {delta.Total,5}");
    Log("");

    if (delta.Total == 0)
    {
        Log("✅ IDENTIDADE: 0 deltas — os dois XSD descrevem o mesmo leiaute.");
    }
    else
    {
        foreach (var g in delta.Entradas.GroupBy(e => e.Kind))
        {
            Log($"── {g.Key} ({g.Count()}) ──────────────────────────────────────────");
            foreach (var e in g.Take(12))
            {
                Log($"   {e.XPath}");
                if (e.Antes is not null) Log($"      antes : {Short(e.Antes, 110)}");
                if (e.Depois is not null) Log($"      depois: {Short(e.Depois, 110)}");
            }
            if (g.Count() > 12) Log($"   … e mais {g.Count() - 12}.");
        }
    }
    Log("");

    var deltaOut = FindArgAfter("--delta-out") ?? Path.Combine(ResolveExportDir(), "xsd-delta.json");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(deltaOut))!);
    File.WriteAllText(deltaOut, delta.ToJson());
    Log($"XsdDelta (JSON) salvo em: {deltaOut}");

    return 0;
}

// Set-diff por path: multiconjuntos de folhas (elementos sem filhos) e atributos.
// FALTA = só no gabarito · SOBRA = só no gerado · TEXTO = valor difere (par ordinal).
(int Falta, int Sobra, int Texto, List<string> Linhas) SetDiffPorPath(XElement esperado, XElement obtido)
{
    var exp = FolhasPorPath(esperado);
    var obt = FolhasPorPath(obtido);
    int falta = 0, sobra = 0, texto = 0;
    var linhas = new List<string>();
    foreach (var path in exp.Keys.Union(obt.Keys).OrderBy(p => p, StringComparer.Ordinal))
    {
        List<string> e = exp.TryGetValue(path, out var le) ? le : new List<string>();
        List<string> o = obt.TryGetValue(path, out var lo) ? lo : new List<string>();
        var n = Math.Min(e.Count, o.Count);
        for (var i = 0; i < n; i++)
        {
            if (e[i] == o[i]) continue;
            texto++;
            linhas.Add($"TEXTO  {path} — esperado='{Short(e[i], 40)}' obtido='{Short(o[i], 40)}'");
        }
        if (e.Count > n) { falta += e.Count - n; linhas.Add($"FALTA  {path} ({e.Count - n}×, esperado ex.: '{Short(e[n], 40)}')"); }
        if (o.Count > n) { sobra += o.Count - n; linhas.Add($"SOBRA  {path} ({o.Count - n}×, obtido ex.: '{Short(o[n], 40)}')"); }
    }
    return (falta, sobra, texto, linhas);
}

// Folhas por caminho completo (sem índice posicional), em ordem de documento.
Dictionary<string, List<string>> FolhasPorPath(XElement raiz)
{
    var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    void Adiciona(string p, string v)
    {
        if (!map.TryGetValue(p, out var l)) map[p] = l = new List<string>();
        l.Add(v);
    }
    void Caminha(XElement el, string path)
    {
        foreach (var a in el.Attributes().Where(a => !a.IsNamespaceDeclaration))
            Adiciona($"{path}/@{a.Name.LocalName}", a.Value.Trim());
        var filhos = el.Elements().ToList();
        if (filhos.Count == 0) { Adiciona(path, el.Value.Trim()); return; }
        foreach (var f in filhos) Caminha(f, $"{path}/{f.Name.LocalName}");
    }
    Caminha(raiz, "/" + raiz.Name.LocalName);
    return map;
}

// Região do diff = 1º segmento abaixo de /infNFe (ide, emit, det, total…).
string Regiao(string xpath)
{
    var parts = xpath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var seg = parts.Length > 1 ? parts[1] : parts.Length > 0 ? parts[0] : "?";
    var idx = seg.IndexOf('[');
    return idx > 0 ? seg[..idx] : seg;
}

// Confere no ROOT o valor absoluto [ini..fim] de um bloco (fatia relativa ao campo).
bool SpotCheck(SpecModel spec, XDocument root, string bloco, int ini, int fim, string esperado, out string obtido)
{
    obtido = "";
    var b = spec.Blocks.FirstOrDefault(x => x.Name == bloco);
    if (b is null) return false;
    var hit = TclGenerator.NamedFields(b).FirstOrDefault(t => t.Field.Inicio <= ini && t.Field.Fim >= fim);
    if (hit.Field is null) return false;
    var val = root.Root!.Elements(bloco).FirstOrDefault()?.Element(hit.Name)?.Value ?? "";
    var rel = ini - hit.Field.Inicio;
    if (rel < 0 || rel + (fim - ini + 1) > val.Length) return false;
    obtido = val.Substring(rel, fim - ini + 1);
    return obtido == esperado;
}

// Clona a árvore aplicando o namespace (só para o oráculo XSD; o diff é sem ns).
XElement ComNamespace(XElement el, XNamespace ns)
{
    var novo = new XElement(ns + el.Name.LocalName);
    foreach (var a in el.Attributes().Where(a => !a.IsNamespaceDeclaration))
        novo.Add(new XAttribute(a.Name.LocalName, a.Value));
    foreach (var n in el.Nodes())
        novo.Add(n is XElement c ? ComNamespace(c, ns) : n);
    return novo;
}

string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
string Csv(string s) => s.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');

string? FindArgAfter(string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--")
        ? args[idx + 1]
        : null;
}

// Resolve o caminho da planilha: valor após "--excel" ou o 1º arg terminando em .xlsx.
string? FindExcelArg()
{
    var idx = Array.IndexOf(args, "--excel");
    if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
        return args[idx + 1];
    return args.FirstOrDefault(a => a.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase));
}

// Sobe da pasta do exe até achar a raiz do repo (que tem ".claude") → .claude/tmp/export.
string ResolveExportDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".claude")))
            return Path.Combine(dir.FullName, ".claude", "tmp", "export");
        dir = dir.Parent;
    }
    return Path.Combine(AppContext.BaseDirectory, "export");
}

// ══════════════════════════════════════════════════════════════════════════
// Fluxo RAG-STATS (P1): constrói o FewShotIndex a partir do corpus real e
// imprime (a) o tamanho do índice por chave tipo|versão|padrão e (b) UMA
// demonstração de recuperação para uma regra DIFÍCIL real (com && ou else)
// do MapperVO usado no fluxo --generate, quando acessível em disco.
// Com --ollama: traduz a regra difícil COM e SEM few-shot e compara.
// ══════════════════════════════════════════════════════════════════════════
async Task<int> RunRagStatsAsync()
{
    var corpusDir = FindArgAfter("--rag") ?? Path.Combine(
        Path.GetDirectoryName(ResolveExportDir())!, "servidor", "layoutparser", "Examples");
    if (!Directory.Exists(corpusDir))
    {
        Log("❌ Pasta de corpus não encontrada.");
        Log("   Uso: dotnet run -- --rag-stats [--rag <pasta-corpus>] [--mapper <mapper.decrypted.xml>]");
        return 2;
    }

    Log("Modo      : RAG-STATS (índice few-shot, P1 — determinístico, sem embeddings)");
    Log($"Corpus    : {corpusDir}");
    Log("");

    var index = FewShotIndex.Build(corpusDir, Log);

    // Regras DSL do mapeador real entram no índice também (DSL análoga + pares fáceis).
    var mapperPath = FindArgAfter("--mapper") ?? FindMapperByClimb();
    XslSynth.Model.MapperVo? mapper = null;
    if (mapperPath is not null && File.Exists(mapperPath))
    {
        try
        {
            mapper = new RealMapperParser().ParseFile(mapperPath);
            index.AddMapper(mapper, Path.GetFileName(mapperPath));
            Log($"[rag] mapeador real indexado: {mapper.Rules.Count} regras DSL ({Path.GetFileName(mapperPath)}).");
        }
        catch (Exception ex)
        {
            Log($"   [aviso] mapeador ilegível ({ex.Message}) — stats só do corpus.");
        }
    }
    else
    {
        Log("   [aviso] MapperVO descriptografado não encontrado — demo de recuperação indisponível.");
    }
    Log("");

    // ── (a) Tamanho do índice por chave ──────────────────────────────────
    Log("── Índice por chave (tipo|versão|padrão) ─────────────────────────");
    Log($"   {"Chave",-28} {"total",5} {"pares",5} {"sóDSL",5} {"sóXSL",5}");
    foreach (var (chave, total, pares, soDsl, soXslt) in index.Stats())
        Log($"   {chave,-28} {total,5} {pares,5} {soDsl,5} {soXslt,5}");
    Log($"   TOTAL: {index.Count} exemplos.");
    Log("");

    // ── (b) Demo de recuperação para UMA regra difícil real ──────────────
    if (mapper is null) return 0;

    var dificil = mapper.Rules.FirstOrDefault(r =>
    {
        var t = FewShotIndex.ClassifyTraits(r.ContentValue);
        return t.HasFlag(DslTraits.CompostaAnd) || t.HasFlag(DslTraits.Else);
    });
    if (dificil is null)
    {
        Log("   [aviso] nenhuma regra com && ou else no mapeador — demo pulada.");
        return 0;
    }

    var traits = FewShotIndex.ClassifyTraits(dificil.ContentValue);
    Log("── Demo de recuperação (regra difícil real) ──────────────────────");
    Log($"   Regra   : {dificil.Name}");
    Log($"   Traços  : {traits} (primário: {FewShotIndex.Rotulo(FewShotIndex.Primario(traits))})");
    Log($"   DSL     : {Short((dificil.ContentValue ?? "").Trim(), 160)}");
    Log("");
    var recuperados = index.Retrieve(dificil, k: 3);
    Log($"   Recuperados: {recuperados.Count} exemplo(s).");
    var n = 0;
    foreach (var ex in recuperados)
    {
        n++;
        var forma = ex is { Dsl: not null, Xslt: not null } ? "par DSL→XSLT"
            : ex.Xslt is not null ? "estilo XSL real" : "DSL análoga";
        Log($"   ── #{n} [{ex.Chave}] {forma} · origem: {ex.Origem}");
        if (ex.Dsl is not null) Log($"      DSL : {Short(ex.Dsl.ReplaceLineEndings(" "), 140)}");
        if (ex.Xslt is not null) Log($"      XSLT: {Short(ex.Xslt.ReplaceLineEndings(" "), 140)}");
    }
    Log("");

    // ── (c) Opcional: tradução da regra difícil COM e SEM few-shot (--ollama) ──
    if (!args.Contains("--ollama"))
    {
        Log("   (passe --ollama para comparar a tradução da regra com e sem few-shot)");
        return 0;
    }
    var client = new OllamaClient(Log);
    if (!await client.IsReachableAsync())
    {
        Log("   [aviso] Ollama indisponível — comparação com/sem few-shot pulada.");
        return 0;
    }

    Log($"── Comparação com/sem few-shot (Ollama {client.Model}) ───────────");
    var sem = await new DslRuleTranslator(client, Log).TranslateAsync(dificil);
    var com = await new DslRuleTranslator(client, Log, index).TranslateAsync(dificil);
    foreach (var (rotulo, tr) in new[] { ("SEM few-shot", sem), ("COM few-shot", com) })
    {
        // Source==Ollama ⇒ o corpo COMPILOU (o tradutor só aceita XSLT que compila).
        var compilou = tr.Source == TranslationSource.Ollama;
        var temChoose = tr.BodyXsl.Contains("xsl:otherwise", StringComparison.Ordinal);
        var temAnd = System.Text.RegularExpressions.Regex.IsMatch(tr.BodyXsl, @"test=""[^""]*\band\b");
        Log($"   {rotulo}: fonte={tr.Source} · XSLT do LLM compilou={(compilou ? "sim" : $"não (caiu para {tr.Source})")}"
            + $" · choose/otherwise={(temChoose ? "sim" : "não")} · test com 'and'={(temAnd ? "sim" : "não")}");
        Log($"      corpo: {Short(tr.BodyXsl.ReplaceLineEndings(" "), 180)}");
    }

    return 0;
}

// Sobe da pasta do exe procurando o MapperVO real (SEM olhar args posicionais —
// aqui o valor após --rag é uma PASTA e não pode ser confundido com o mapeador).
string? FindMapperByClimb()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName,
            ".claude", "tmp", "export", "MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.decrypted.xml");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

// ══════════════════════════════════════════════════════════════════════════
// Fluxo REAL: MapperVO Sysmiddle descriptografado → candidato XSLT + cobertura
// (verificação POSSÍVEL sem gabarito de runtime: compila + % de cobertura)
// ══════════════════════════════════════════════════════════════════════════
async Task<int> RunRealAsync()
{
    var useOllama = args.Contains("--ollama");

    var mapperPath = FindRealMapper();
    if (mapperPath is null || !File.Exists(mapperPath))
    {
        Log("❌ Mapeador real não encontrado.");
        Log("   Esperado: .claude/tmp/export/MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.decrypted.xml");
        Log("   Ou passe o caminho: dotnet run -- <mapper.decrypted.xml>");
        return 2;
    }

    Log($"Modo      : MAPEADOR REAL ({(useOllama ? "tradução via Ollama" : "tradução OFFLINE / fallback determinístico")})");
    Log($"Mapeador  : {mapperPath}");
    Log("");

    // ── Passo 1: parse do MapperVO real (encoding utf-16 declarado, bytes UTF-8) ──
    var mapper = new RealMapperParser().ParseFile(mapperPath);
    Log($"MapperVO  : {mapper.Name}");
    Log($"   LinkMappings (diretos) : {mapper.LinkMappings.Count}");
    Log($"   Rules (DSL Sysmiddle)  : {mapper.Rules.Count}");
    Log("");

    // ── Passo 2: transpila os LinkMappings → folhas XSLT (determinístico, sem IA) ──
    var links = new LinkMappingTranspiler().Transpile(mapper);
    Log($"[1] LinkMappings → folhas XSLT : {links.Count} transpilados, {links.Skipped} sem folha derivável.");

    // ── Passo 3: traduz as Rules DSL → XSLT (Ollama opcional; senão fallback) ──
    OllamaClient? ollama = null;
    if (useOllama)
    {
        var client = new OllamaClient(Log);
        if (await client.IsReachableAsync())
        {
            ollama = client;
            Log($"[2] Ollama disponível ({client.Model}) — traduzindo {mapper.Rules.Count} regras (pode demorar)...");
        }
        else
        {
            Log("[2] Ollama indisponível — caindo para o fallback determinístico OFFLINE.");
        }
    }

    // Índice RAG few-shot (P1) — opt-in via --rag <pasta-corpus>; sem a flag o
    // comportamento (e o prompt) permanecem idênticos.
    FewShotIndex? fewShot = null;
    var ragDir = FindArgAfter("--rag");
    if (ragDir is not null)
    {
        if (Directory.Exists(ragDir))
        {
            fewShot = FewShotIndex.Build(ragDir, Log);
            Log($"[rag] few-shot habilitado: {fewShot.Count} exemplos indexados de {ragDir}.");
        }
        else
        {
            Log($"   [aviso] pasta de corpus '{ragDir}' não existe — seguindo SEM few-shot.");
        }
    }

    // Interpretador determinístico (multi-saída) primeiro; só cai no tradutor 1-saída
    // (fallback simples ou Ollama) quando o padrão dominante não é reconhecido.
    var interpreter = new DslBlockInterpreter();
    var translator = new DslRuleTranslator(ollama, Log, fewShot);
    var translations = new List<RuleTranslation>(mapper.Rules.Count);
    int rulesInterpreted = 0, rulesFallback = 0, rulesOllama = 0, rulesStub = 0;

    foreach (var rule in mapper.Rules)
    {
        var emissions = interpreter.Interpret(rule);
        if (emissions.Count > 0)
        {
            translations.AddRange(emissions);
            rulesInterpreted++;
            continue;
        }

        var tr = await translator.TranslateAsync(rule);
        translations.Add(tr);
        switch (tr.Source)
        {
            case TranslationSource.Ollama: rulesOllama++; break;
            case TranslationSource.MockFallback: rulesFallback++; break;
            default: rulesStub++; break;
        }
    }

    var emInterp = translations.Count(t => t.Source == TranslationSource.DslInterpreter);
    Log($"[2] Rules DSL → XSLT (por REGRA, total {mapper.Rules.Count}):");
    Log($"      {rulesInterpreted} interpretadas (multi-saída) → {emInterp} emissões guardadas");
    Log($"      {rulesOllama} via Ollama, {rulesFallback} via fallback 1-saída, {rulesStub} ainda stub (padrão não reconhecido).");
    var traduzidas = rulesInterpreted + rulesOllama + rulesFallback;
    Log($"      ⇒ {traduzidas}/{mapper.Rules.Count} regras com tradução real; {rulesStub} pendentes.");
    Log("");

    // ── Passo 4: monta o candidato único (folhas de link + nós de regra) ──
    var rootName = DetermineRoot(mapper);
    var (candidate, stats) = new CandidateBuilder().Build(rootName, links.Leaves, translations);
    Log($"[3] Candidato XSLT (raiz <{rootName}>): {stats.RuleNodes} nós de regra, "
        + $"{stats.LinkLeaves} folhas de link, {stats.VarsDeclared} variáveis declaradas.");

    // ── Passo 5: valida COMPILAÇÃO + COBERTURA (sem gabarito de runtime) ──
    var report = new CoverageValidator().Validate(candidate, mapper);
    Log("");
    Log("── Relatório de cobertura ────────────────────────────────────────");
    Log($"   Candidato COMPILA (XslCompiledTransform) : {(report.Compiles ? "sim ✅" : "não ❌")}");
    if (!report.Compiles) Log($"      erro: {report.CompileError}");
    Log($"   Cobertura de LinkMappings : {report.LinksCovered}/{report.LinksTotal} ({report.LinkPct})");
    Log($"   Cobertura de Rules        : {report.RulesCovered}/{report.RulesTotal} ({report.RulePct})");
    Log("");

    // ── Passo 6: grava o candidato para inspeção ──────────────────────────
    var outPath = Path.Combine(Path.GetDirectoryName(mapperPath)!, "candidate.xslt");
    candidate.Save(outPath);
    Log($"Candidato salvo em: {outPath}");
    Log("");

    Log("── Limite honesto (o que falta p/ fechar o loop diff==0) ─────────");
    Log("   • select do input é SIMBÓLICO (token do GUID) — precisa do catálogo GUID→XPath.");
    Log("   • sem gabarito de runtime ainda: validamos COMPILAÇÃO + COBERTURA, não igualdade.");
    Log("   • o gabarito virá do host FiatMQ (ver docs/architecture/ia-xslt-synthesis.md §9).");

    return report.Compiles ? 0 : 1;
}

// Raiz de saída = primeiro segmento mais frequente entre os paths T. das regras.
string DetermineRoot(MapperVo m)
{
    var root = m.Rules
        .Select(r => r.TargetPath)
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => Xslt.Segments(p!).FirstOrDefault())
        .Where(s => !string.IsNullOrEmpty(s))
        .GroupBy(s => s)
        .OrderByDescending(g => g.Count())
        .Select(g => g.Key!)
        .FirstOrDefault();
    return root ?? "nfeProc";
}

// Resolve o caminho do mapeador real: arg explícito (ignorando VALORES de flags,
// ex.: a pasta após --rag), senão sobe da pasta do exe procurando
// .claude/tmp/export/MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.decrypted.xml.
string? FindRealMapper()
{
    var flagValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var flag in new[] { "--rag", "--mapper", "--xsd", "--txt", "--xml",
                 "--excel", "--catalog", "--generate", "--debug-ref" })
        if (FindArgAfter(flag) is { } v) flagValues.Add(v);

    var explicitArg = args.FirstOrDefault(a => !a.StartsWith("--") && !flagValues.Contains(a));
    if (explicitArg is not null && File.Exists(explicitArg)) return explicitArg;

    return FindMapperByClimb() ?? explicitArg;
}

// ══════════════════════════════════════════════════════════════════════════
// Fluxo SAMPLE: exemplo sintético embutido + loop de reparo diff==0 (MVP)
// ══════════════════════════════════════════════════════════════════════════
async Task<int> RunSampleAsync()
{
    var useOllama = args.Contains("--ollama")
        || string.Equals(Environment.GetEnvironmentVariable("XSLSYNTH_SYNTH"), "ollama",
            StringComparison.OrdinalIgnoreCase);

    var sampleDir = Path.Combine(AppContext.BaseDirectory, "sample");
    var mapperPath = Path.Combine(sampleDir, "mapper.xml");
    var inputPath = Path.Combine(sampleDir, "input.xml");
    var expectedPath = Path.Combine(sampleDir, "expected.xml");
    var xsdPath = Path.Combine(sampleDir, "schema.xsd");

    // Passo 1: extrair o MapperVO sintético.
    var mapper = new MapperExtractor().ExtractFromFile(mapperPath);
    Log($"Mapeador: {mapper.Name} ({mapper.MapperGuid})");
    Log($"   LinkMappings (diretos): {mapper.LinkMappings.Count} | Rules: {mapper.Rules.Count}");
    Log("");

    var input = XDocument.Load(inputPath);
    var expected = File.ReadAllText(expectedPath);

    IXslSynthesizer synthesizer = useOllama
        ? new OllamaXslSynthesizer(Log)
        : new MockXslSynthesizer();
    Log($"Sintetizador: {synthesizer.Name}");
    Log("");

    var orchestrator = new RepairOrchestrator();
    var report = await orchestrator.RunAsync(mapper, input, expected, xsdPath, synthesizer, Log);

    Log("");
    Log("── Métricas ──────────────────────────────────────────────────────");
    Log($"   Cobertura determinística : {report.MappedFields}/{report.TotalFields} campos");
    Log($"   Iterações do loop        : {report.Iterations}");
    Log($"   Diffs residuais          : {report.FinalDiffs.Count}");
    Log($"   XSD válido               : {(report.FinalXsd.IsValid ? "sim" : "não")}");
    Log("");

    if (report.Converged)
    {
        Log("✅ CONVERGIU (diff == 0 e XSD válido).");
        Log("");
        Log("── XSLT final aprovado ───────────────────────────────────────────");
        Log(report.FinalXslt);
        return 0;
    }

    Log("❌ NÃO convergiu dentro do limite de iterações.");
    Log("");
    Log("── Última saída produzida ────────────────────────────────────────");
    Log(report.FinalOutput);
    return 1;
}
