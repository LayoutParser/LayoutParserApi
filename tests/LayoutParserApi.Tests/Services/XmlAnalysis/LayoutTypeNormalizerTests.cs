using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.XmlAnalysis;

using Xunit;

namespace LayoutParserApi.Tests.Services.XmlAnalysis
{
    /// <summary>
    /// Cobre a issue #219 — gate `generate-for-layout` recusava o layout FIAT
    /// `LAY_TXT_MQSERIES_ENVNFE_4.00_NFe` com "Tipo de layout não suportado: 2", porque
    /// `tbLayout.LayoutType` (coluna SQL crua) carrega um código numérico legado do Sysmiddle em vez
    /// do texto esperado ("TextPositional"/"XML").
    /// </summary>
    public class LayoutTypeNormalizerTests
    {
        [Fact]
        public void ResolveEffectiveLayoutType_ComValorJaTexto_UsaDiretoSemFallback()
        {
            var layout = new LayoutRecord { LayoutType = "TextPositional" };

            var fallbackChamado = false;
            var result = LayoutTypeNormalizer.ResolveEffectiveLayoutType(layout, _ => fallbackChamado = true);

            Assert.Equal("TextPositional", result);
            Assert.False(fallbackChamado);
        }

        [Fact]
        public void ResolveEffectiveLayoutType_ComValorXmlTipo_UsaDiretoSemFallback()
        {
            var layout = new LayoutRecord { LayoutType = "XML" };

            var fallbackChamado = false;
            var result = LayoutTypeNormalizer.ResolveEffectiveLayoutType(layout, _ => fallbackChamado = true);

            Assert.Equal("XML", result);
            Assert.False(fallbackChamado);
        }

        [Fact]
        public void ResolveEffectiveLayoutType_ComCodigoNumericoEXmlDescriptografadoDivergente_UsaValorDoXml()
        {
            // Caso real da issue #219: tbLayout.LayoutType = "2" (código numérico legado), mas o XML
            // descriptografado do layout (fonte autoritativa, mesma usada por
            // LayoutDatabaseService.IsTextPositionalLayout) diz corretamente "TextPositional".
            var layout = new LayoutRecord
            {
                Name = "LAY_TXT_MQSERIES_ENVNFE_4.00_NFe",
                LayoutGuid = Guid.Parse("ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c"),
                LayoutType = "2",
                DecryptedContent = "<LayoutVO><LayoutType>TextPositional</LayoutType></LayoutVO>"
            };

            string? mensagemFallback = null;
            var result = LayoutTypeNormalizer.ResolveEffectiveLayoutType(layout, msg => mensagemFallback = msg);

            Assert.Equal("TextPositional", result);
            Assert.NotNull(mensagemFallback);
            Assert.Contains("LayoutType='2'", mensagemFallback);
            Assert.Contains("TextPositional", mensagemFallback);
        }

        [Fact]
        public void ResolveEffectiveLayoutType_ComCodigoNumericoDoisEXmlAninhadoEmLayoutVoFilho_UsaValorDoXml()
        {
            // Mesma divergência, mas com o XML tendo LayoutVO como elemento filho do root (variação de
            // estrutura já tratada por LayoutDatabaseService.IsTextPositionalLayout).
            var layout = new LayoutRecord
            {
                LayoutType = "2",
                DecryptedContent = "<Root><LayoutVO><LayoutType>XML</LayoutType></LayoutVO></Root>"
            };

            var result = LayoutTypeNormalizer.ResolveEffectiveLayoutType(layout);

            Assert.Equal("XML", result);
        }

        [Fact]
        public void ResolveEffectiveLayoutType_ComCodigoNumericoDoisSemXmlLegivel_CaiNoFallbackHeuristico()
        {
            // Sem conteúdo XML disponível para confirmar — usa a heurística documentada (não
            // confirmada pelo dono) baseada no padrão observado na issue #219.
            var layout = new LayoutRecord
            {
                Name = "LAY_SEM_XML",
                LayoutType = "2",
                DecryptedContent = "",
                ValueContent = ""
            };

            string? mensagemFallback = null;
            var result = LayoutTypeNormalizer.ResolveEffectiveLayoutType(layout, msg => mensagemFallback = msg);

            Assert.Equal("TextPositional", result);
            Assert.NotNull(mensagemFallback);
            Assert.Contains("NÃO FOI CONFIRMADA", mensagemFallback);
        }

        [Fact]
        public void ResolveEffectiveLayoutType_ComTipoDesconhecidoSemXml_DevolveValorCruOriginal()
        {
            // Código sem nenhuma evidência (nem "2", nem XML legível) — não inventa, devolve o valor
            // cru para o chamador decidir (hoje: rejeita com "Tipo de layout não suportado").
            var layout = new LayoutRecord
            {
                LayoutType = "99",
                DecryptedContent = ""
            };

            var fallbackChamado = false;
            var result = LayoutTypeNormalizer.ResolveEffectiveLayoutType(layout, _ => fallbackChamado = true);

            Assert.Equal("99", result);
            Assert.False(fallbackChamado);
        }

        [Fact]
        public void ResolveEffectiveLayoutType_ComXmlMalformado_NaoLancaECaiNoFallbackOuValorCru()
        {
            var layout = new LayoutRecord
            {
                LayoutType = "2",
                DecryptedContent = "<Root><LayoutVO><LayoutType>Text" // XML incompleto/malformado
            };

            var exception = Record.Exception(() => LayoutTypeNormalizer.ResolveEffectiveLayoutType(layout));

            Assert.Null(exception);
        }
    }
}
