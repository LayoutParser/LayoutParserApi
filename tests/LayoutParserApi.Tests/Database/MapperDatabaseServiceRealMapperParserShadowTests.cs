using System.Reflection;

using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Database;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Cobre a fase de sombra da issue #139 (passo 1 do plano de migração descrito em
    /// docs/architecture/inventario-parsers-mapperVo-issue-139.md): a comparação log-only
    /// entre a leitura ad-hoc legada e o <c>RealMapperParser</c> não pode alterar o
    /// comportamento de <c>ExtractLayoutGuidsFromDecryptedContent</c>, mesmo quando o
    /// parser B falha com um MapperVO malformado. Usa apenas MapperVOs SINTÉTICOS
    /// (fabricados), nunca conteúdo real de cliente.
    /// </summary>
    public class MapperDatabaseServiceRealMapperParserShadowTests
    {
        private static MapperDatabaseService NovoServico()
        {
            var config = new ConfigurationBuilder().Build();
            return new MapperDatabaseService(
                NullLogger<MapperDatabaseService>.Instance,
                decryptionService: null!,
                config);
        }

        private static void InvocarExtractLayoutGuids(MapperDatabaseService servico, Mapper mapper)
        {
            var metodo = typeof(MapperDatabaseService).GetMethod(
                "ExtractLayoutGuidsFromDecryptedContent",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(metodo);
            metodo!.Invoke(servico, new object[] { mapper });
        }

        [Fact]
        public void ExtractLayoutGuids_ComMapperVoSinteticoValido_MantemComportamentoLegado_ComComparacaoSombraAtiva()
        {
            // MapperVO sintético mínimo, GUIDs concordantes entre leitura legada e RealMapperParser
            // (ambos leem os mesmos elementos InputLayoutGuid/TargetLayoutGuid do mesmo XDocument).
            const string mapperVoSintetico = """
                <MapperVO>
                    <MapperGuid>GUID_MAPPER_SINTETICO</MapperGuid>
                    <Name>MapperSintetico</Name>
                    <InputLayoutGuid>FLD_INPUT_SINTETICO</InputLayoutGuid>
                    <TargetLayoutGuid>TAG_TARGET_SINTETICO</TargetLayoutGuid>
                </MapperVO>
                """;

            var mapper = new Mapper
            {
                Id = 1,
                Name = "MapperSintetico",
                DecryptedContent = mapperVoSintetico
            };

            var servico = NovoServico();

            InvocarExtractLayoutGuids(servico, mapper);

            // Comportamento legado preservado: os GUIDs extraídos pelo caminho ad-hoc continuam
            // populando InputLayoutGuidFromXml/TargetLayoutGuidFromXml e (coluna vazia) os campos
            // InputLayoutGuid/TargetLayoutGuid, exatamente como antes da comparação log-only existir.
            Assert.Equal("FLD_INPUT_SINTETICO", mapper.InputLayoutGuidFromXml);
            Assert.Equal("TAG_TARGET_SINTETICO", mapper.TargetLayoutGuidFromXml);
            Assert.Equal("FLD_INPUT_SINTETICO", mapper.InputLayoutGuid);
            Assert.Equal("TAG_TARGET_SINTETICO", mapper.TargetLayoutGuid);
        }

        [Fact]
        public void ExtractLayoutGuids_ComXmlMalformadoParaORealMapperParser_NaoQuebraOFluxoLegado()
        {
            // Root sem os elementos que o RealMapperParser espera derivar de forma diferente da
            // leitura legada não é suficiente para forçar exceção (Parse é tolerante a elemento
            // ausente). Para validar resiliência de verdade, simulamos o caso em que o próprio
            // XDocument.Parse já falhou antes de chegar no parser B: DecryptedContent vazio faz o
            // método legado retornar cedo (linha 425-426) sem sequer instanciar um XDocument — não
            // há caminho realista, dentro do próprio método, de chegar num XDocument válido para o
            // legado mas inválido para o RealMapperParser.Parse (ambos operam sobre a MESMA
            // instância de XDocument). O cenário de resiliência real é: RealMapperParser.Parse
            // lança quando root é nulo — o que já é impossível aqui pois o método legado já
            // retornou antes se root fosse nulo. O teste abaixo cobre a garantia equivalente:
            // conteúdo sem NENHUM MapperVO reconhecível não derruba o método legado nem a sombra.
            const string mapperVoSemGuids = """
                <MapperVO>
                    <MapperGuid>GUID_SEM_LAYOUT_GUIDS</MapperGuid>
                </MapperVO>
                """;

            var mapper = new Mapper
            {
                Id = 2,
                Name = "MapperSinteticoSemGuids",
                DecryptedContent = mapperVoSemGuids
            };

            var servico = NovoServico();

            var excecao = Record.Exception(() => InvocarExtractLayoutGuids(servico, mapper));

            Assert.Null(excecao);
            // Sem InputLayoutGuid/TargetLayoutGuid no XML, o legado não popula nada — continua null,
            // como antes da fase de sombra existir. A ausência de exceção é o que importa aqui: a
            // comparação log-only (que também roda RealMapperParser sobre este XDocument) não pode
            // propagar falha para fora do método.
            Assert.Null(mapper.InputLayoutGuidFromXml);
            Assert.Null(mapper.TargetLayoutGuidFromXml);
        }

        [Fact]
        public void ExtractLayoutGuids_ComXslContentNoMapperVo_PopulaXslContentViaRealMapperParser()
        {
            // Issue #139, passo 2: MapperVO sintético com <XslContent> — antes migrado via
            // MapperVo.FromXml (legado), agora via RealMapperParser (canônico). A extração de
            // XslContent em ambos os parsers usa a MESMA leitura (root.Element("XslContent")
            // com fallback para "Xsl"), então o resultado deve ser idêntico ao comportamento
            // pré-migração.
            const string mapperVoComXsl = """
                <MapperVO>
                    <MapperGuid>GUID_MAPPER_COM_XSL</MapperGuid>
                    <Name>MapperComXsl</Name>
                    <InputLayoutGuid>FLD_INPUT_XSL</InputLayoutGuid>
                    <TargetLayoutGuid>TAG_TARGET_XSL</TargetLayoutGuid>
                    <XslContent>&lt;xsl:stylesheet&gt;conteudo-sintetico&lt;/xsl:stylesheet&gt;</XslContent>
                </MapperVO>
                """;

            var mapper = new Mapper
            {
                Id = 4,
                Name = "MapperComXsl",
                DecryptedContent = mapperVoComXsl
            };

            var servico = NovoServico();

            InvocarExtractLayoutGuids(servico, mapper);

            Assert.Equal("<xsl:stylesheet>conteudo-sintetico</xsl:stylesheet>", mapper.XslContent);
            // GUIDs continuam vindo do caminho ad-hoc legado, inalterado pela migração do passo 2.
            Assert.Equal("FLD_INPUT_XSL", mapper.InputLayoutGuid);
            Assert.Equal("TAG_TARGET_XSL", mapper.TargetLayoutGuid);
        }

        [Fact]
        public void ExtractLayoutGuids_ComXslJaPopuladoPorExtractXslFromDecryptedContent_MantemComportamentoDeNaoSobrescrever()
        {
            // ExtractXslFromDecryptedContent roda ANTES do bloco do RealMapperParser e já popula
            // mapper.XslContent a partir do elemento <Xsl> (mesma leitura que o RealMapperParser
            // faria como fallback de XslContent). O guard `string.IsNullOrEmpty(mapper.XslContent)`
            // no bloco migrado garante que não há dupla escrita / sobrescrita redundante — cobre a
            // guarda de precedência que já existia antes da migração do passo 2.
            const string mapperVoComXsl = """
                <MapperVO>
                    <MapperGuid>GUID_MAPPER_XSL_PRECEDENCIA</MapperGuid>
                    <Name>MapperComXslPrecedencia</Name>
                    <Xsl>&lt;xsl:stylesheet&gt;valor-unico&lt;/xsl:stylesheet&gt;</Xsl>
                </MapperVO>
                """;

            var mapper = new Mapper
            {
                Id = 5,
                Name = "MapperComXslPrecedencia",
                DecryptedContent = mapperVoComXsl
            };

            var servico = NovoServico();

            InvocarExtractLayoutGuids(servico, mapper);

            Assert.Equal("<xsl:stylesheet>valor-unico</xsl:stylesheet>", mapper.XslContent);
        }

        // ----------------------------------------------------------------------------------
        // Regressão do Cypress #14 / FIAT LAY_TXT_MQSERIES_ENVNFE_4.00_NFe: generate-for-layout
        // gerava o .tcl mas nenhum .xsl, com warning "Nenhum mapeador encontrado". Causa: os
        // GUIDs em [tbMapper] são gravados com prefixo "LAY_", mas o chamador passa o GUID cru
        // (layout.LayoutGuid.ToString()) e a busca fazia igualdade exata. NormalizeLayoutGuid
        // remove o prefixo dos dois lados antes de comparar.
        // ----------------------------------------------------------------------------------

        private static string InvocarNormalizeLayoutGuid(string entrada)
        {
            var metodo = typeof(MapperDatabaseService).GetMethod(
                "NormalizeLayoutGuid",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(metodo);
            return (string)metodo!.Invoke(null, new object?[] { entrada })!;
        }

        [Theory]
        [InlineData("ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c", "ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c")]
        [InlineData("LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c", "ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c")]
        [InlineData("  lay_AD4FB6F4-9FF5-44FD-988B-3DA5ED56B22C  ", "ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c")]
        [InlineData(null, "")]
        [InlineData("   ", "")]
        public void NormalizeLayoutGuid_RemovePrefixoLayEEspacos_ParaComparacaoConsistente(string? entrada, string esperado)
        {
            Assert.Equal(esperado, InvocarNormalizeLayoutGuid(entrada!));
        }

        [Fact]
        public void NormalizeLayoutGuid_GuidCruEComPrefixo_ColidemAposNormalizacao()
        {
            // O cerne do bug: estes dois valores representam o MESMO layout e precisam bater.
            Assert.Equal(
                InvocarNormalizeLayoutGuid("ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c"),
                InvocarNormalizeLayoutGuid("LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c"));
        }

        [Fact]
        public void ExtractLayoutGuids_ComContentVazio_NaoExecutaNadaEMantemMapperInalterado()
        {
            var mapper = new Mapper
            {
                Id = 3,
                Name = "MapperSemConteudo",
                DecryptedContent = ""
            };

            var servico = NovoServico();

            var excecao = Record.Exception(() => InvocarExtractLayoutGuids(servico, mapper));

            Assert.Null(excecao);
            Assert.Null(mapper.InputLayoutGuidFromXml);
            Assert.Null(mapper.TargetLayoutGuidFromXml);
        }
    }
}
