using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;

using LayoutParserApi.Services.Transformation.LowCode;

using Xunit;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Fase 0 do contrato de rastreabilidade TXT↔XML (issue #138/#126) — cobertura de
    /// <see cref="SysmiddleSectionMappingResolver"/>. Todo MapeadorVO/XML usado aqui é SINTÉTICO
    /// (nunca dado real de cliente), no mesmo shape do MapeadorVO decifrado real (ver
    /// RealMapperParserRealShapeTests para o parser em si).
    /// </summary>
    public class SysmiddleSectionMappingResolverTests
    {
        private const string OutputXmlSample = """
            <NFe xmlns="http://www.portalfiscal.inf.br/nfe">
              <infNFe>
                <emit>
                  <xNome>EMPRESA TESTE</xNome>
                </emit>
                <det>
                  <prod><cProd>1</cProd></prod>
                </det>
                <det>
                  <prod><cProd>2</cProd></prod>
                </det>
              </infNFe>
            </NFe>
            """;

        private static string BuildMapperXml(params (string name, string dsl)[] rules)
        {
            var rulesXml = string.Join("\n", rules.Select((r, i) => $"""
                <Rule>
                  <Name>{r.name}</Name>
                  <Sequence>{i + 1}</Sequence>
                  <ElementGuid>RUL_{i + 1}</ElementGuid>
                  <TargetElementGuid>TAG_{i + 1}</TargetElementGuid>
                  <ContentValue>{System.Security.SecurityElement.Escape(r.dsl)}</ContentValue>
                </Rule>
                """));

            return $"""
                <MapperVO>
                  <MapperGuid>MAP_teste</MapperGuid>
                  <Name>Mapper Teste</Name>
                  <InputLayoutGuid>LAY_input</InputLayoutGuid>
                  <TargetLayoutGuid>LAY_target</TargetLayoutGuid>
                  <Rules>
                    {rulesXml}
                  </Rules>
                  <LinkMappings />
                </MapperVO>
                """;
        }

        [Fact]
        public void Resolve_ComRegraEstrutural_ProduzMappingAuthoritativeComXPathENamespace()
        {
            var mapperXml = BuildMapperXml(
                ("NomeDoEmitente_xNome", "T.NFe/infNFe/emit/xNome = I.LINHA_EMIT/NOME"));

            var (mappings, namespaces) = SysmiddleSectionMappingResolver.Resolve(mapperXml, OutputXmlSample);

            var mapping = Assert.Single(mappings);
            Assert.Equal("authoritative", mapping.Confidence);
            Assert.Equal("LINHA_EMIT", mapping.Source.LineName);
            Assert.Equal("RUL_1", mapping.Source.LineGuid);
            Assert.Equal(1, mapping.Source.LineOccurrence);

            var target = Assert.Single(mapping.Targets);
            Assert.Equal("/nfe:NFe/nfe:infNFe/nfe:emit/nfe:xNome", target.XPath);
            Assert.Equal("element", target.NodeKind);

            Assert.NotNull(namespaces);
            Assert.Equal("http://www.portalfiscal.inf.br/nfe", namespaces!["nfe"]);

            // Gate QA (@lp-qa): não basta o XPath TER o formato esperado — ele precisa de fato
            // RESOLVER, via um motor XPath real, para o nó correto dentro do XML de saída do
            // próprio candidato (não só bater string). Usa XPathSelectElement (System.Xml.XPath)
            // com o XmlNamespaceManager derivado do dicionário `namespaces` retornado — o mesmo
            // par (XPath, namespaces) que o front-end receberia no contrato real.
            var outputDoc = XDocument.Parse(OutputXmlSample);
            var nsManager = new System.Xml.XmlNamespaceManager(new System.Xml.NameTable());
            foreach (var kv in namespaces!)
                nsManager.AddNamespace(kv.Key, kv.Value);

            var resolvedNode = outputDoc.XPathSelectElement(target.XPath, nsManager);
            Assert.NotNull(resolvedNode);
            Assert.Equal("xNome", resolvedNode!.Name.LocalName);
            Assert.Equal("EMPRESA TESTE", resolvedNode.Value);
        }

        [Fact]
        public void Resolve_ComGrupoRepetido_IncrementaLineOccurrencePorRegraEmSequencia()
        {
            // Mesma linha de origem (LINHA_DET) e MESMO targetPath (grupo repetido modelado como
            // N regras distintas do mapper, ver limitação documentada no resolver) — cada regra
            // deve receber uma LineOccurrence crescente.
            var mapperXml = BuildMapperXml(
                ("CodigoProduto_cProd_1", "T.NFe/infNFe/det/prod/cProd = I.LINHA_DET/COD"),
                ("CodigoProduto_cProd_2", "T.NFe/infNFe/det/prod/cProd = I.LINHA_DET/COD"));

            var (mappings, _) = SysmiddleSectionMappingResolver.Resolve(mapperXml, OutputXmlSample);

            Assert.Equal(2, mappings.Count);
            Assert.Equal(1, mappings[0].Source.LineOccurrence);
            Assert.Equal(2, mappings[1].Source.LineOccurrence);
            // Ambos os alvos resolvem o mesmo XPath (mesma estrutura declarada) — a saída real tem
            // 2 nós <det>, então a contagem estrutural no XML de saída reflete isso.
            Assert.Equal(2, mappings[0].Targets[0].XmlOccurrence);
            Assert.Equal(2, mappings[1].Targets[0].XmlOccurrence);

            // Gate QA: confirma resolução real via motor XPath — como o XPath não é indexado
            // (não distingue qual dos 2 <det> é qual), XPathSelectElements deve encontrar AMBOS os
            // nós <cProd> existentes no XML de saída, um por <det>.
            var outputDoc = XDocument.Parse(OutputXmlSample);
            var nsManager = new System.Xml.XmlNamespaceManager(new System.Xml.NameTable());
            nsManager.AddNamespace("nfe", "http://www.portalfiscal.inf.br/nfe");
            var resolvedNodes = outputDoc.XPathSelectElements(mappings[0].Targets[0].XPath, nsManager).ToList();
            Assert.Equal(2, resolvedNodes.Count);
            Assert.All(resolvedNodes, n => Assert.Equal("cProd", n.Name.LocalName));
            Assert.Equal(new[] { "1", "2" }, resolvedNodes.Select(n => n.Value));
        }

        [Fact]
        public void Resolve_SemRegrasComTargetPathDeclarado_RetornaListaVaziaNaoNull()
        {
            // Regra existe, mas sem "T.<path>" reconhecível na DSL — não deve virar mapping
            // aproximado; deve simplesmente não entrar no array (pathway suporta, [] é a resposta).
            var mapperXml = BuildMapperXml(("SemPath", "algumaCoisaQueNaoEhAtribuicaoDeDestino"));

            var (mappings, namespaces) = SysmiddleSectionMappingResolver.Resolve(mapperXml, OutputXmlSample);

            Assert.Empty(mappings);
            // Sem mapping resolvido, ainda assim o XML de saída é legível — namespaces continua
            // sendo reportado (é uma propriedade do XML, não dos mappings).
            Assert.NotNull(namespaces);
        }

        [Fact]
        public void Resolve_MapperOuXmlIlegivel_DegradaParaListaVaziaSemLancar()
        {
            var (mappings1, ns1) = SysmiddleSectionMappingResolver.Resolve(null, OutputXmlSample);
            Assert.Empty(mappings1);
            Assert.Null(ns1);

            var (mappings2, ns2) = SysmiddleSectionMappingResolver.Resolve("<xml não fechado", OutputXmlSample);
            Assert.Empty(mappings2);
            Assert.Null(ns2);

            var mapperXml = BuildMapperXml(("X", "T.NFe/infNFe/emit/xNome = I.LINHA_EMIT/NOME"));
            var (mappings3, ns3) = SysmiddleSectionMappingResolver.Resolve(mapperXml, "<xml não fechado");
            Assert.Empty(mappings3);
            Assert.Null(ns3);
        }
    }
}
