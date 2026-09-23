using System.Xml.Linq;
using System.Xml.Xsl;
using XslSynth.Core;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace XslSynth.Core.Tests;

/// <summary>
/// Issue #438 — casca do documento gerado (caminho de PRODUÇÃO: CandidateBuilder) e regra de
/// campo declarada no DSL (Concat/Substring). Estrutura espelha o caso real de inutilização
/// (inutNFe/versao/xmlns/infInut/xJust) — valores sintéticos, sem dado de cliente.
/// </summary>
public sealed class DocumentShellTests
{
    private static MapperRule Rule(string name, string dsl, string? targetType = null)
        => new()
        {
            Name = name,
            ContentValue = "%beginRuleContent;\n" + dsl + "\n%endRuleContent;",
            TargetPath = RealMapperParser.TargetPathFromDsl(dsl),
            TargetType = targetType,
        };

    private static IReadOnlyList<RuleTranslation> Interpret(params MapperRule[] rules)
    {
        var interp = new DslBlockInterpreter();
        return rules.SelectMany(r => interp.Interpret(r)).ToList();
    }

    private static string Transform(XDocument xslt, string inputXml)
    {
        var t = new XslCompiledTransform();
        using (var r = xslt.CreateReader()) t.Load(r, XsltSettings.Default, null);
        using var sw = new StringWriter();
        using var xw = System.Xml.XmlWriter.Create(sw, new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true });
        t.Transform(XDocument.Parse(inputXml).CreateReader(), xw);
        xw.Flush();
        return sw.ToString();
    }

    // ── Interpretador: Concat / Substring / GetLength ────────────────────────

    [Fact]
    public void Interpreter_PrefixoMaisTruncamento_TraduzConcatSubstring()
    {
        var rule = Rule("Rule_xJust",
            "T.inutNFe/infInut/xJust = Concat('Justificativa Inutilizacao:', Substring(I.ROOT/Header/xJust, 0, 227));");

        var em = new DslBlockInterpreter().Interpret(rule);

        var only = Assert.Single(em);
        Assert.Equal("inutNFe/infInut/xJust", only.TargetPath);
        // DSL é 0-based, XPath 1-based: Substring(x,0,227) -> substring(x,1,227).
        Assert.Contains("concat('Justificativa Inutilizacao:',substring(ROOT/Header/xJust,1,227))", only.BodyXsl);
        Assert.True(XsltFragment.Compiles(only.BodyXsl, out var err), err);
    }

    [Fact]
    public void Interpreter_LiteralComVirgulaEParenteses_NaoQuebraOSplitDeArgumentos()
    {
        var rule = Rule("R", "T.a/b = Concat('x, (y)', I.L/c, 'z');");
        var only = Assert.Single(new DslBlockInterpreter().Interpret(rule));
        Assert.Contains("concat('x, (y)',L/c,'z')", only.BodyXsl);
    }

    [Fact]
    public void Interpreter_GetLength_ViraStringLength()
    {
        var only = Assert.Single(new DslBlockInterpreter().Interpret(Rule("R", "T.a/b = GetLength(I.L/c);")));
        Assert.Contains("string-length(L/c)", only.BodyXsl);
    }

    [Theory]
    [InlineData("T.a/b = Trim(I.L/c);")]                       // Trim != normalize-space: não inventa
    [InlineData("T.a/b = Substring(I.L/c, #.n, 3);")]           // início não literal
    [InlineData("T.a/b = Concat('a\\'b', I.L/c);")]             // literal com escape
    [InlineData("T.a/b = Concat(I.L/c, );")]                    // argumento vazio
    public void Interpreter_ForaDoSubconjunto_NaoEmiteNadaEmVezDeChutar(string dsl)
    {
        Assert.Empty(new DslBlockInterpreter().Interpret(Rule("R", dsl)));
    }

    // ── CandidateBuilder: xmlns/versao viram casca, não elemento ─────────────

    private const string Ns = "http://www.portalfiscal.inf.br/nfe";

    private static IReadOnlyList<RuleTranslation> InutRules() => Interpret(
        Rule("Rule_xmlns", $"T.inutNFe/xmlns = '{Ns}';", "ATT"),
        Rule("Rule_versao", "T.inutNFe/versao = '4.00';", "ATT"),
        Rule("Rule_xServ", "T.inutNFe/infInut/xServ = 'INUTILIZAR';", "TAG"),
        Rule("Rule_xJust", "T.inutNFe/infInut/xJust = Concat('Justificativa Inutilizacao:', Substring(I.ROOT/Header/xJust, 0, 227));", "TAG"));

    [Fact]
    public void CandidateBuilder_XmlnsEVersao_ViramNamespaceEAtributo_NaoElementos()
    {
        var (doc, stats) = new CandidateBuilder().Build("inutNFe", Array.Empty<XElement>(), InutRules());

        var content = doc.ToString();
        Assert.DoesNotContain("<xmlns", content);
        Assert.DoesNotContain("<versao", content);
        Assert.Equal(Ns, stats.Shell!.Namespace);
        Assert.Contains("inutNFe/versao", stats.Shell.Attributes);
        Assert.Empty(stats.Limitations!);

        // Prova real: aplica o XSLT gerado a um input e confere o documento de saída.
        var output = Transform(doc, "<ROOT><Header><xJust>motivo qualquer</xJust></Header></ROOT>");
        var result = XElement.Parse(output);
        XNamespace ns = Ns;
        Assert.Equal(ns + "inutNFe", result.Name);
        Assert.Equal("4.00", (string?)result.Attribute("versao"));
        Assert.Equal("INUTILIZAR", (string?)result.Element(ns + "infInut")!.Element(ns + "xServ"));
        Assert.Equal("Justificativa Inutilizacao:motivo qualquer", (string?)result.Element(ns + "infInut")!.Element(ns + "xJust"));
    }

    [Fact]
    public void CandidateBuilder_XJustTruncaEm227()
    {
        var (doc, _) = new CandidateBuilder().Build("inutNFe", Array.Empty<XElement>(), InutRules());
        var longo = new string('x', 300);
        var result = XElement.Parse(Transform(doc, $"<ROOT><Header><xJust>{longo}</xJust></Header></ROOT>"));
        XNamespace ns = Ns;
        var xJust = (string)result.Element(ns + "infInut")!.Element(ns + "xJust")!;
        Assert.Equal("Justificativa Inutilizacao:".Length + 227, xJust.Length);
    }

    [Fact]
    public void CandidateBuilder_XmlnsNaoConstante_NaoChutaNamespace_RegistraLimitacao()
    {
        // xmlns condicionado a um campo do input: valor não é constante -> não pode virar namespace.
        var tr = Interpret(Rule("Rule_xmlns", "#.v = I.L/ns;\nif(IsNullOrEmpty(#.v) != True())\nbegin\nT.inutNFe/xmlns = #.v;\nend", "ATT"));
        var (doc, stats) = new CandidateBuilder().Build("inutNFe", Array.Empty<XElement>(), tr);

        Assert.Null(stats.Shell!.Namespace);
        Assert.DoesNotContain("<xmlns", doc.ToString());
        Assert.Contains(stats.Limitations!, l => l.Contains("não é constante"));
    }

    [Fact]
    public void CandidateBuilder_NamespaceComPrefixo_EDescartadoComLimitacao()
    {
        var tr = Interpret(Rule("Rule_xsi", "T.inutNFe/xmlns_xsi = 'http://www.w3.org/2001/XMLSchema-instance';", "ATT"));
        var (doc, stats) = new CandidateBuilder().Build("inutNFe", Array.Empty<XElement>(), tr);

        Assert.DoesNotContain("xmlns_xsi", doc.ToString());
        Assert.Contains(stats.Limitations!, l => l.Contains("xmlns_xsi"));
    }

    [Fact]
    public void CandidateBuilder_AtributoEmElementoIntermediario_VaiParaOPaiCerto_AntesDosFilhos()
    {
        var tr = Interpret(
            Rule("R1", "T.enviNFe/NFe/infNFe/ide/cUF = '35';", "TAG"),
            Rule("R2", "T.enviNFe/NFe/infNFe/Id = 'NFe123';", "ATT"),
            Rule("R3", "T.enviNFe/NFe/infNFe/versao = '4.00';", "ATT"));
        var (doc, _) = new CandidateBuilder().Build("enviNFe", Array.Empty<XElement>(), tr);

        var infNFe = doc.Descendants().First(e => e.Name.LocalName == "infNFe");
        var kids = infNFe.Elements().Select(e => e.Name.LocalName == "attribute" ? "@" + (string?)e.Attribute("name") : e.Name.LocalName).ToList();
        Assert.Equal(new[] { "@Id", "@versao", "ide" }, kids); // atributos primeiro (XSLT exige)

        var output = XElement.Parse(Transform(doc, "<x/>"));
        var inf = output.Descendants("infNFe").Single();
        Assert.Equal("NFe123", (string?)inf.Attribute("Id"));
        Assert.Equal("4.00", (string?)inf.Attribute("versao"));
    }

    [Fact]
    public void CandidateBuilder_AtributoReconhecidoPeloLayoutDeDestino_SemTargetTypeNaRegra()
    {
        // TargetType da regra ausente/TAG, mas o LayoutVO de destino declara `versao` como AttributeElementVO.
        const string layout = """
            <LayoutVO xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="XmlLayoutVO">
              <LayoutGuid>LAY_x</LayoutGuid>
              <Elements>
                <Element xsi:type="GroupTagElementVO">
                  <ElementGuid>GRT_1</ElementGuid><Name>inutNFe</Name>
                  <Elements>
                    <Element xsi:type="AttributeElementVO"><ElementGuid>ATT_1</ElementGuid><Name>versao</Name></Element>
                    <Element xsi:type="TagElementVO"><ElementGuid>TAG_2</ElementGuid><Name>infInut</Name><Elements/></Element>
                  </Elements>
                </Element>
              </Elements>
            </LayoutVO>
            """;
        var catalog = GuidXPathCatalog.LoadFromXml(layout);
        Assert.Equal("inutNFe", DocumentShellOptions.SingleRoot(catalog));

        var tr = Interpret(Rule("R", "T.inutNFe/versao = '4.00';", "TAG"));
        var (doc, stats) = new CandidateBuilder().Build("inutNFe", Array.Empty<XElement>(), tr, DocumentShellOptions.From(catalog));

        Assert.DoesNotContain("<versao", doc.ToString());
        Assert.Contains("inutNFe/versao", stats.Shell!.Attributes);

        // Sem o catálogo, o mesmo caso continua como elemento (limite honesto: nada a inferir).
        var (docSem, _) = new CandidateBuilder().Build("inutNFe", Array.Empty<XElement>(), tr);
        Assert.Contains("<versao", docSem.ToString());
    }

    [Fact]
    public void SingleRoot_LayoutMultiRaiz_EhNull()
    {
        const string layout = """
            <LayoutVO xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="TextLayoutVO">
              <LayoutGuid>LAY_t</LayoutGuid>
              <Elements>
                <Element xsi:type="GroupTagElementVO"><ElementGuid>GRT_1</ElementGuid><Name>LINHA001</Name><Elements/></Element>
                <Element xsi:type="GroupTagElementVO"><ElementGuid>GRT_2</ElementGuid><Name>LINHA002</Name><Elements/></Element>
              </Elements>
            </LayoutVO>
            """;
        Assert.Null(DocumentShellOptions.SingleRoot(GuidXPathCatalog.LoadFromXml(layout)));
        Assert.Null(DocumentShellOptions.SingleRoot(null));
    }

    [Fact]
    public void CoverageValidator_ContaAtributoENamespaceComoCobertos()
    {
        var mapper = new MapperVo();
        var rules = InutRules();
        foreach (var r in rules.Select(t => t.Rule).Distinct()) mapper.Rules.Add(r);
        var (doc, _) = new CandidateBuilder().Build("inutNFe", Array.Empty<XElement>(), rules);

        var report = new CoverageValidator().Validate(doc, mapper);

        Assert.True(report.Compiles, report.CompileError);
        Assert.Equal(4, report.RulesTotal);
        Assert.Equal(4, report.RulesCovered); // xmlns, versao, xServ, xJust
    }
}
