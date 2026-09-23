using XslSynth.Core.StructuralResolution;
using XslSynth.Model;

namespace XslSynth.Core.Tests.StructuralResolution;

/// <summary>
/// Testes do item 1 (issue #140, design §2.1) contra o XSD REAL da NF-e (mirror
/// nfephp-org/sped-nfe, PL_009_V4 — mesmo pacote já usado em <c>sefaz-xsd-schema-source</c>,
/// ver memória de @lp-parser-llm). Fixtures: só os 3 arquivos XSD públicos necessários
/// (nfe_v4.00.xsd + leiauteNFe_v4.00.xsd + tiposBasico_v4.00.xsd + xmldsig-core-schema_v1.01.xsd)
/// — nenhum dado de documento real, só estrutura de schema pública.
/// </summary>
public sealed class XmlLayoutStructureParserTests
{
    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "StructuralResolution", "fixtures", fileName);

    private static XmlLayoutCatalog LoadNfeCatalog()
    {
        var parser = new XmlLayoutStructureParser();
        var root = parser.Parse(FixturePath("nfe_v4.00.xsd"), "NFe");
        return new XmlLayoutCatalog(root);
    }

    [Fact]
    public void Parse_RaizNFe_TemNamespacePortalFiscal()
    {
        var catalog = LoadNfeCatalog();

        Assert.Equal("NFe", catalog.Root.Name);
        Assert.Equal("http://www.portalfiscal.inf.br/nfe", catalog.Root.Namespace);
        Assert.Equal(XmlNodeKind.Element, catalog.Root.Kind);
    }

    [Fact]
    public void Parse_ResolveCaminhoCompleto_AteCfopDoProduto()
    {
        var catalog = LoadNfeCatalog();

        var cfop = catalog.TryResolveByPath("NFe/infNFe/det/prod/CFOP");

        Assert.NotNull(cfop);
        Assert.Equal(XmlNodeKind.Element, cfop!.Kind);
    }

    [Fact]
    public void Parse_ConstroiXPathAbsoluto_ComPrefixoDeNamespace()
    {
        var catalog = LoadNfeCatalog();
        var cfop = catalog.TryResolveByPath("NFe/infNFe/det/prod/CFOP")!;

        var xpath = catalog.BuildAbsoluteXPath(cfop);

        Assert.Equal("/nfe:NFe/nfe:infNFe/nfe:det/nfe:prod/nfe:CFOP", xpath);
    }

    [Fact]
    public void Parse_NoDet_TemMaxOccursMaiorQue1_RepetivelPelaNFe()
    {
        var catalog = LoadNfeCatalog();
        var det = catalog.TryResolveByPath("NFe/infNFe/det")!;

        Assert.True(det.IsRepeatable);
        Assert.True(det.MaxOccurs is null || det.MaxOccurs > 1);
    }

    [Fact]
    public void Parse_ResolveAtributo_ComoNoDeAtributo()
    {
        var catalog = LoadNfeCatalog();

        // infNFe/@Id é atributo obrigatório do infNFe na NF-e real.
        var idAttribute = catalog.TryResolveByPath("NFe/infNFe/@Id");

        Assert.NotNull(idAttribute);
        Assert.Equal(XmlNodeKind.Attribute, idAttribute!.Kind);
    }

    [Fact]
    public void Parse_AncestraisDoCfop_IncluemDetRepetido()
    {
        var catalog = LoadNfeCatalog();
        var cfop = catalog.TryResolveByPath("NFe/infNFe/det/prod/CFOP")!;

        var ancestors = catalog.Ancestors(cfop).ToList();

        Assert.Contains(ancestors, a => a.Name == "det" && a.IsRepeatable);
    }

    [Fact]
    public void ResolveByLeafName_NomeAmbiguo_RetornaMultiplosCandidatos()
    {
        var catalog = LoadNfeCatalog();

        // "vProd" aparece em vários pontos da árvore de totais/itens da NF-e — ambíguo por design.
        var candidates = catalog.ResolveByLeafName("vProd");

        Assert.True(candidates.Count > 1, "esperado nome de folha ambíguo na NF-e real");
    }

    [Fact]
    public void ResolveByLeafName_NomeUnico_RetornaUmCandidato()
    {
        var catalog = LoadNfeCatalog();

        var candidates = catalog.ResolveByLeafName("natOp");

        Assert.Single(candidates);
    }
}
