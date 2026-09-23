using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Transformation;

/// <summary>
/// Issue #139 (passo 2): valida que a migração de <see cref="XslGeneratorService"/> do parser
/// legado (<c>LayoutParserApi.Models.Entities.MapperVo.FromXml</c>) para o parser B
/// (<c>XslSynth.Model.MapperVo</c> via <c>XslSynth.Core.RealMapperParser</c>) preserva o
/// comportamento observável para um MapperVO SINTÉTICO (nunca dado real de cliente, ver
/// docs/architecture/inventario-parsers-mapperVo-issue-139.md).
///
/// Não compara "antes" vs "depois" via dois codepaths (o parser legado foi removido do
/// consumidor) — em vez disso, fixa o XSL gerado pelo estado migrado como snapshot esperado,
/// documentando explicitamente os campos exercitados (Rules + LinkMappings, ambos os ramos
/// tipados e o fallback via XML bruto).
/// </summary>
public class XslGeneratorServiceMapperVoMigrationTests
{
    private const string SyntheticMapperXml = """
        <MapperVO>
            <MapperGuid>MAP_SYNTH_0001</MapperGuid>
            <Name>MapperSinteticoTeste139</Name>
            <Description>Mapeador sintetico para teste de migracao issue 139</Description>
            <InputLayoutGuid>LAY_INPUT_SYNTH</InputLayoutGuid>
            <TargetLayoutGuid>LAY_TARGET_SYNTH</TargetLayoutGuid>
            <IsNotExecuteTargetParser>false</IsNotExecuteTargetParser>
            <Rules>
                <Rule>
                    <ElementGuid>ELM_0001</ElementGuid>
                    <Description>Regra sintetica de mapeamento direto</Description>
                    <Sequence>1</Sequence>
                    <Name>RegraCabecalho</Name>
                    <IsRequired>true</IsRequired>
                    <ContentValue>I.LINHA000/Campo = T.enviNFe/NFe/infNFe/ide/cUF;</ContentValue>
                    <CreateOnlyChildren>false</CreateOnlyChildren>
                    <IsPrePosRule>false</IsPrePosRule>
                    <TargetElementGuid>TAG_CUF</TargetElementGuid>
                </Rule>
            </Rules>
            <LinkMappings>
                <LinkMappingItem>
                    <ElementGuid>ELM_0002</ElementGuid>
                    <Description>LinkMapping sintetico</Description>
                    <Sequence>1</Sequence>
                    <Name>Descricao_xMun</Name>
                    <IsRequired>false</IsRequired>
                    <InputLayoutGuid>FLD_MUNICIPIO</InputLayoutGuid>
                    <TargetLayoutGuid>TAG_XMUN</TargetLayoutGuid>
                    <IsToTruncateValue>false</IsToTruncateValue>
                    <RemoveWhiteSpaceType>None</RemoveWhiteSpaceType>
                    <DefaultValue></DefaultValue>
                    <NotCreateGroupTagOnlyChilds>false</NotCreateGroupTagOnlyChilds>
                    <AllowEmpty>true</AllowEmpty>
                </LinkMappingItem>
            </LinkMappings>
        </MapperVO>
        """;

    [Fact]
    public async Task GenerateXslFromMapAsync_ComMapperVoSinteticoTipado_ProcessaRulesELinkMappings()
    {
        // Arrange
        var service = new XslGeneratorService(NullLogger<XslGeneratorService>.Instance);
        var mapPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(mapPath, SyntheticMapperXml);

        try
        {
            // Act
            var xsl = await service.GenerateXslFromMapAsync(mapPath);

            // Assert: estrutura basica do XSL sempre presente
            Assert.Contains("<xsl:stylesheet", xsl);
            Assert.Contains("<xsl:template match=\"/\">", xsl);
            Assert.Contains("<enviNFe", xsl);
            Assert.Contains("<infNFe versao=\"4.00\">", xsl);

            // Assert: Rule tipada (via RealMapperParser) foi processada - gera elemento cUF
            // com xsl:value-of para o XPath convertido de I.LINHA000/Campo
            Assert.Contains("<cUF>", xsl);
            Assert.Contains("ROOT/LINHA000/Campo", xsl);

            // Assert: LinkMapping tipado (via RealMapperParser) foi processado - nome do
            // elemento eh derivado do sufixo apos "_" (convencao Sysmiddle: Descricao_tag)
            Assert.Contains("<xMun>", xsl);
            Assert.Contains("LinkMapping: Descricao_xMun", xsl);
            Assert.Contains("InputGuid: FLD_MUNICIPIO", xsl);
            Assert.Contains("TargetGuid: TAG_XMUN", xsl);
        }
        finally
        {
            File.Delete(mapPath);
        }
    }

    [Fact]
    public async Task GenerateXslFromMapAsync_ComXmlSemMapperVoReconhecivel_UsaFallbackDeXmlBruto()
    {
        // Arrange: XML sem a estrutura MapperVO esperada pelo RealMapperParser (root diferente),
        // mas com Rule/LinkMappingItem soltos no documento - exercita o caminho de fallback
        // (mapperVo == null) que já existia antes da migração.
        const string mapWithoutMapperVoRoot = """
            <OutraCoisa>
                <Rule>
                    <Name>RegraFallback</Name>
                    <ContentValue>I.LINHA001/CampoX = T.enviNFe/NFe/infNFe/dest/xNome;</ContentValue>
                </Rule>
                <LinkMappingItem>
                    <Name>Descricao_cMun</Name>
                    <InputLayoutGuid>FLD_CMUN</InputLayoutGuid>
                    <TargetLayoutGuid>TAG_CMUN</TargetLayoutGuid>
                    <AllowEmpty>true</AllowEmpty>
                </LinkMappingItem>
            </OutraCoisa>
            """;

        var service = new XslGeneratorService(NullLogger<XslGeneratorService>.Instance);
        var mapPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(mapPath, mapWithoutMapperVoRoot);

        try
        {
            // Act
            var xsl = await service.GenerateXslFromMapAsync(mapPath);

            // Assert: XSL ainda eh gerado (fallback nao quebra)
            Assert.Contains("<xsl:stylesheet", xsl);

            // Assert: LinkMapping do fallback (ProcessLinkMappingsFromXml) foi processado
            Assert.Contains("<cMun>", xsl);
            Assert.Contains("LinkMapping: Descricao_cMun", xsl);
        }
        finally
        {
            File.Delete(mapPath);
        }
    }
}
