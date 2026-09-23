using XslSynth.Core;

namespace XslSynth.Core.Tests;

/// <summary>
/// Issue #425 — generalização de <see cref="GuidXPathCatalog"/> para o endpoint de árvore dupla
/// (ADR "adr-layout-tree-endpoint-425"): extração de cardinalidade (<c>MinimalOccurrence</c>/
/// <c>MaximumOccurrence</c>) e <see cref="GuidXPathCatalog.BuildTree"/> (hierarquia recursiva,
/// em vez do dicionário achatado que já existia para o loop de síntese XSLT).
/// </summary>
public sealed class GuidXPathCatalogTreeTests
{
    // LayoutVO mínimo, mas com a mesma forma (xsi:type por nó) do arquivo real exportado do
    // Connect Us: um grupo com filhos (elemento simples + atributo) e um wrapper Sequence
    // envolvendo dois elementos irmãos — cobre hierarquia aninhada, atributo e achatamento de wrapper.
    private const string LayoutVoXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Layout xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <LayoutGuid>LAY_767be1dd-0000-0000-0000-000000000001</LayoutGuid>
  <Elements>
    <Element xsi:type=""GroupTagElementVO"">
      <ElementGuid>GRT_root</ElementGuid>
      <Name>enviNFe</Name>
      <MinimalOccurrence>1</MinimalOccurrence>
      <MaximumOccurrence>1</MaximumOccurrence>
      <Elements>
        <Element xsi:type=""AttributeElementVO"">
          <ElementGuid>ATT_versao</ElementGuid>
          <Name>versao</Name>
        </Element>
        <Element xsi:type=""SequenceElementVO"">
          <Name>Sequence</Name>
          <Elements>
            <Element xsi:type=""TagElementVO"">
              <ElementGuid>TAG_nfe</ElementGuid>
              <Name>NFe</Name>
              <MinimalOccurrence>1</MinimalOccurrence>
              <MaximumOccurrence>999</MaximumOccurrence>
            </Element>
            <Element xsi:type=""TagElementVO"">
              <ElementGuid>TAG_protNFe</ElementGuid>
              <Name>protNFe</Name>
              <MinimalOccurrence>0</MinimalOccurrence>
              <MaximumOccurrence>1</MaximumOccurrence>
            </Element>
          </Elements>
        </Element>
      </Elements>
    </Element>
  </Elements>
</Layout>";

    [Fact]
    public void BuildTree_ArvoreAninhada_PreservaHierarquiaCompleta()
    {
        var (layoutGuid, roots) = GuidXPathCatalog.BuildTree(LayoutVoXml);

        Assert.Equal("LAY_767be1dd-0000-0000-0000-000000000001", layoutGuid);
        var root = Assert.Single(roots);
        Assert.Equal("GRT_root", root.ElementGuid);
        Assert.Equal("enviNFe", root.Name);
        Assert.Equal("group", root.Kind);
    }

    [Fact]
    public void BuildTree_Atributo_VemComoFilhoDoGrupoComKindAttribute()
    {
        var (_, roots) = GuidXPathCatalog.BuildTree(LayoutVoXml);
        var root = roots[0];

        var atributo = root.Children.Single(c => c.ElementGuid == "ATT_versao");
        Assert.Equal("attribute", atributo.Kind);
        Assert.Equal("versao", atributo.Name);
        Assert.Empty(atributo.Children);
    }

    [Fact]
    public void BuildTree_WrapperSequence_AchataFilhosComoIrmaosDoGrupo()
    {
        var (_, roots) = GuidXPathCatalog.BuildTree(LayoutVoXml);
        var root = roots[0];

        // O SequenceElementVO NÃO deve virar nó — NFe/protNFe entram direto como filhos de enviNFe,
        // junto com o atributo "versao" (3 filhos no total, não 2 — o wrapper "some").
        Assert.Equal(3, root.Children.Count);
        Assert.Contains(root.Children, c => c.ElementGuid == "TAG_nfe");
        Assert.Contains(root.Children, c => c.ElementGuid == "TAG_protNFe");
        Assert.DoesNotContain(root.Children, c => c.Name == "Sequence");
    }

    [Fact]
    public void BuildTree_Cardinalidade_ExtraidaDeMinimalEMaximalOccurrence()
    {
        var (_, roots) = GuidXPathCatalog.BuildTree(LayoutVoXml);
        var root = roots[0];

        var nfe = root.Children.Single(c => c.ElementGuid == "TAG_nfe");
        Assert.Equal(1, nfe.MinOccurs);
        Assert.Equal(999, nfe.MaxOccurs);

        var protNfe = root.Children.Single(c => c.ElementGuid == "TAG_protNFe");
        Assert.Equal(0, protNfe.MinOccurs);
        Assert.Equal(1, protNfe.MaxOccurs);
    }

    [Fact]
    public void BuildTree_NoSemOcorrenciaDeclarada_DegradaParaNull()
    {
        // O atributo "versao" no fixture não declara MinimalOccurrence/MaximumOccurrence.
        var (_, roots) = GuidXPathCatalog.BuildTree(LayoutVoXml);
        var atributo = roots[0].Children.Single(c => c.ElementGuid == "ATT_versao");

        Assert.Null(atributo.MinOccurs);
        Assert.Null(atributo.MaxOccurs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildTree_ConteudoVazio_DegradaParaArvoreVazia(string? xml)
    {
        var (layoutGuid, roots) = GuidXPathCatalog.BuildTree(xml);

        Assert.Null(layoutGuid);
        Assert.Empty(roots);
    }

    [Fact]
    public void BuildTree_XmlMalformado_DegradaParaArvoreVaziaSemLancar()
    {
        var (layoutGuid, roots) = GuidXPathCatalog.BuildTree("<Layout><NaoFecha>");

        Assert.Null(layoutGuid);
        Assert.Empty(roots);
    }

    [Fact]
    public void LoadFromXml_MesmoConteudo_ProduzMesmosGuidsQueOArquivoOriginalLeria()
    {
        // Regressão: LoadFromXml precisa continuar populando MinOccurs/MaxOccurs no dicionário
        // achatado (GuidXPathEntry), não só na árvore nova — consumidores existentes (loop XSLT)
        // continuam usando o catálogo flat.
        var catalog = GuidXPathCatalog.LoadFromXml(LayoutVoXml);

        Assert.True(catalog.TryResolve("TAG_nfe", out var entry));
        Assert.Equal(1, entry.MinOccurs);
        Assert.Equal(999, entry.MaxOccurs);
        Assert.Equal("enviNFe/NFe", entry.XPath);
    }
}
