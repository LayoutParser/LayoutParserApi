using System.Xml.Linq;
using System.Xml.Xsl;
using XslSynth.Core;
using XslSynth.Metrics;

namespace XslSynth.Core.Tests;

/// <summary>
/// Issue #438, caminho (a) — refino determinístico da saída do LLM por exemplos recuperados:
/// casca do documento (template raiz, elemento raiz, namespace, versao) e regra de campo
/// demonstrada (xJust = prefixo + truncamento). Fixtures sintéticas com a MESMA forma do caso real
/// NFe006c_InutNFe (sem dado de cliente, sem o prefixo de extensão "ng").
/// </summary>
public sealed class ExampleCandidateRefinerTests
{
    private const string Xsl = "http://www.w3.org/1999/XSL/Transform";

    private static string Example(string version, string xjustExpr = "normalize-space(concat('Justificativa Inutilizacao:',substring(normalize-space(ROOT/Header/xJust),'1','227')))",
        string ns = "http://www.portalfiscal.inf.br/nfe", string root = "inutNFe", string leafParent = "infInut")
        => $"""
            <?xml version='1.0' encoding='ISO-8859-1' ?>
            <xsl:stylesheet version="1.0" xmlns:xsl="{Xsl}">
              <xsl:output method="xml" encoding="UTF-8"/>
              <xsl:template match="/">
                <{root} xmlns="{ns}">
                  <xsl:attribute name="versao">{version}</xsl:attribute>
                  <{leafParent}>
                    <xsl:attribute name="Id"><xsl:value-of select="concat('ID',ROOT/Header/cUF)"/></xsl:attribute>
                    <tpAmb><xsl:value-of select="ROOT/Header/tpAmb"/></tpAmb>
                    <xJust><xsl:value-of select="{xjustExpr}"/></xJust>
                  </{leafParent}>
                </{root}>
              </xsl:template>
            </xsl:stylesheet>
            """;

    // Saída típica do modelo pequeno: miolo certo, SEM template/raiz/namespace/versao e com xJust cópia simples.
    private const string BaselineCandidate = $"""
        <?xml version='1.0' encoding='ISO-8859-1' ?>
        <xsl:stylesheet version="1.0" xmlns:xsl="{Xsl}">
          <xsl:output method="xml" encoding="UTF-8"/>
          <infInut>
            <xsl:attribute name="Id"><xsl:value-of select="concat('ID',ROOT/Header/cUF)"/></xsl:attribute>
            <tpAmb><xsl:value-of select="ROOT/Header/tpAmb"/></tpAmb>
            <xJust><xsl:value-of select="ROOT/Header/xJust"/></xJust>
          </infInut>
        </xsl:stylesheet>
        """;

    private const string Input = "<ROOT><Header><cUF>35</cUF><tpAmb>2</tpAmb><xJust>motivo</xJust></Header></ROOT>";

    private static XElement Run(string xslt)
    {
        var t = new XslCompiledTransform();
        using (var r = XDocument.Parse(xslt).CreateReader()) t.Load(r);
        using var sw = new StringWriter();
        using var xw = System.Xml.XmlWriter.Create(sw, new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true });
        t.Transform(XDocument.Parse(Input).CreateReader(), xw);
        xw.Flush();
        return XElement.Parse(sw.ToString());
    }

    private static RefinerExample Ex(string id, string version, double sim, string xslt) => new(id, version, sim, xslt);

    [Fact]
    public void CascaERegra_SaidaRefinadaProduzMesmoDocumentoQueOExemploReal()
    {
        var ex = Ex("NFe/2.06c/InutNFe", "2.06c", 0.995, Example("2.00"));

        var r = ExampleCandidateRefiner.Refine(BaselineCandidate, "2.06b", new[] { ex });

        Assert.True(r.Changed);
        Assert.Contains(r.Actions, a => a.Contains("namespace"));
        Assert.Contains(r.Actions, a => a.Contains("versao=\"2.00\""));
        Assert.Contains(r.Actions, a => a.Contains("regra: <xJust>"));
        Assert.Empty(r.Limitations);

        // Validação REAL: o XSLT refinado, aplicado ao mesmo input, gera o mesmo documento do gabarito.
        Assert.True(XNode.DeepEquals(Run(Example("2.00")), Run(r.Xslt)),
            $"esperado:\n{Run(Example("2.00"))}\nobtido:\n{Run(r.Xslt)}");
    }

    [Fact]
    public void Sem_Refino_CandidatoCruNaoTemCascaNemNamespace()
    {
        // Documenta a falha original: a saída crua nem carrega como stylesheet válido (conteúdo solto).
        Assert.ThrowsAny<Exception>(() => Run(BaselineCandidate));
    }

    [Fact]
    public void VersaoDeOutraFamilia_NaoEChutada_MasCascaENamespaceSaem()
    {
        var ex = Ex("NFe/4.00/InutNFe", "4.00", 0.9, Example("4.00"));

        var r = ExampleCandidateRefiner.Refine(BaselineCandidate, "2.06b", new[] { ex });

        var doc = Run(r.Xslt);
        XNamespace ns = "http://www.portalfiscal.inf.br/nfe";
        Assert.Equal(ns + "inutNFe", doc.Name);
        Assert.Null(doc.Attribute("versao"));
        Assert.Contains(r.Limitations, l => l.Contains("versao") && l.Contains("família"));
    }

    [Fact]
    public void VersaoDivergenteNaMesmaFamilia_Omitida()
    {
        var exs = new[]
        {
            Ex("a", "2.06b", 0.9, Example("2.00")),
            Ex("b", "2.06c", 0.8, Example("2.10")),
        };
        var r = ExampleCandidateRefiner.Refine(BaselineCandidate, "2.06b", exs);
        Assert.Null(Run(r.Xslt).Attribute("versao"));
        Assert.Contains(r.Limitations, l => l.Contains("divergem"));
    }

    [Fact]
    public void RegraComOrigemDiferente_NaoETransplantada()
    {
        // O exemplo usa outra origem (ROOT/xJust/xJust): mesma etiqueta, mas NÃO é a mesma regra demonstrada.
        var ex = Ex("pipeline", "2.06c", 0.9,
            Example("2.00", "normalize-space(concat('Justificativa:',substring(ROOT/xJust/xJust,'1','227')))"));

        var r = ExampleCandidateRefiner.Refine(BaselineCandidate, "2.06b", new[] { ex });

        Assert.Equal("motivo", (string)Run(r.Xslt).Descendants().First(e => e.Name.LocalName == "xJust"));
        Assert.DoesNotContain(r.Actions, a => a.StartsWith("regra:"));
    }

    [Fact]
    public void ExemplosQueDiscordam_MantemCopiaSimples_ERegistraAmbiguidade()
    {
        var exs = new[]
        {
            Ex("com-regra", "2.06c", 0.9, Example("2.00")),
            Ex("sem-regra", "2.06b", 0.8, Example("2.00", "ROOT/Header/xJust")),
        };
        var r = ExampleCandidateRefiner.Refine(BaselineCandidate, "2.06b", exs);

        Assert.Equal("motivo", (string)Run(r.Xslt).Descendants().First(e => e.Name.LocalName == "xJust"));
        Assert.Contains(r.Limitations, l => l.Contains("xJust") && l.Contains("ambígua"));
    }

    [Fact]
    public void ExemploDeEstruturaIncompativel_NaoAlteraNada_ERegistraLimitacao()
    {
        var ex = Ex("cancelamento", "2.06c", 0.9, Example("2.00", root: "cancNFe", leafParent: "infCanc"));

        var r = ExampleCandidateRefiner.Refine(BaselineCandidate, "2.06b", new[] { ex });

        Assert.False(r.Changed);
        Assert.Equal(BaselineCandidate, r.Xslt);
        Assert.Contains(r.Limitations, l => l.Contains("Casca do documento não determinada"));
    }

    [Fact]
    public void NamespacesDivergentesEntreExemplos_NaoChuta()
    {
        var exs = new[]
        {
            Ex("a", "2.06b", 0.9, Example("2.00", ns: "http://www.portalfiscal.inf.br/nfe")),
            Ex("b", "2.06b", 0.8, Example("2.00", ns: "http://www.portalfiscal.inf.br/cte")),
        };
        var r = ExampleCandidateRefiner.Refine(BaselineCandidate, "2.06b", exs);

        Assert.Equal(XNamespace.None, Run(r.Xslt).Name.Namespace);
        Assert.Contains(r.Limitations, l => l.Contains("Namespace") && l.Contains("divergente"));
    }

    [Fact]
    public void CandidatoJaCompleto_FicaIntacto()
    {
        var completo = Example("2.00");
        var r = ExampleCandidateRefiner.Refine(completo, "2.06b", new[] { Ex("a", "2.06c", 0.9, completo) });
        Assert.False(r.Changed);
        Assert.Equal(completo, r.Xslt);
    }

    [Fact]
    public void CandidatoMalformado_NaoLanca()
    {
        var r = ExampleCandidateRefiner.Refine("<xsl:stylesheet", "2.06b", Array.Empty<RefinerExample>());
        Assert.False(r.Changed);
        Assert.Single(r.Limitations);
    }

    // ── Métrica campo a campo ───────────────────────────────────────────────

    [Fact]
    public void OutputValidator_CamposIdenticos_AntesEDepoisDoRefino()
    {
        var gabarito = Example("2.00");
        var ex = Ex("NFe/2.06c/InutNFe", "2.06c", 0.995, gabarito);

        var antes = OutputValidator.Validate(BaselineCandidate, gabarito);
        var refinado = ExampleCandidateRefiner.Refine(BaselineCandidate, "2.06b", new[] { ex }).Xslt;
        var depois = OutputValidator.Validate(refinado, gabarito);

        Assert.Equal(4, antes.FieldsTotal);          // @versao, @Id, tpAmb, xJust
        Assert.Equal(2, antes.FieldsIdenticalLoose); // @Id e tpAmb (xJust difere; versao ausente)
        Assert.Equal(0, antes.FieldsIdenticalStrict); // sem raiz, nenhum caminho completo bate

        Assert.Equal(4, depois.FieldsIdenticalLoose);
        Assert.Equal(4, depois.FieldsIdenticalStrict);
        Assert.True(depois.TagOverlapRatio >= antes.TagOverlapRatio);
    }
}
