using LayoutParserApi.Services.Transformation.LowCode;

namespace LayoutParserApi.Tests.Transformation
{
    public class LowCodeTransformationEligibilityTests
    {
        [Theory]
        [InlineData("mqseries")]
        [InlineData("idoc")]
        [InlineData("unknown")]
        [InlineData("txt")]
        [InlineData("IDOC")]
        public void Tipo_da_allowlist_e_elegivel_sem_diferenciar_maiusculas(string detectedType)
        {
            var result = LowCodeTransformationEligibility.Evaluate(
                parseSucceeded: true,
                layoutGuid: "LAY_SYNTHETIC",
                rawText: "CONTEUDO SINTETICO",
                detectedType: detectedType,
                isXmlInput: false);

            Assert.True(result.IsEligible, $"O tipo posicional {detectedType} deve entrar no gate.");
            Assert.Null(result.Reason);
        }

        [Fact]
        public void Xml_nao_e_elegivel()
        {
            var result = LowCodeTransformationEligibility.Evaluate(
                parseSucceeded: true,
                layoutGuid: "LAY_SYNTHETIC",
                rawText: "<root />",
                detectedType: "xml",
                isXmlInput: true);

            Assert.False(result.IsEligible);
            Assert.Equal("type_not_positional", result.Reason);
        }

        [Fact]
        public void Conteudo_vazio_nao_e_elegivel()
        {
            var result = LowCodeTransformationEligibility.Evaluate(
                parseSucceeded: true,
                layoutGuid: "LAY_SYNTHETIC",
                rawText: "  ",
                detectedType: "idoc",
                isXmlInput: false);

            Assert.False(result.IsEligible);
            Assert.Equal("empty_input", result.Reason);
        }

        [Fact]
        public void Parse_com_falha_nao_e_elegivel()
        {
            var result = LowCodeTransformationEligibility.Evaluate(
                parseSucceeded: false,
                layoutGuid: "LAY_SYNTHETIC",
                rawText: "CONTEUDO SINTETICO",
                detectedType: "idoc",
                isXmlInput: false);

            Assert.False(result.IsEligible);
            Assert.Equal("structural_error", result.Reason);
        }

        [Fact]
        public void Layout_sem_guid_nao_e_elegivel_e_informa_ausencia_de_mapper()
        {
            var result = LowCodeTransformationEligibility.Evaluate(
                parseSucceeded: true,
                layoutGuid: "  ",
                rawText: "CONTEUDO SINTETICO",
                detectedType: "idoc",
                isXmlInput: false);

            Assert.False(result.IsEligible);
            Assert.Equal("no_mapper", result.Reason);
        }

        [Theory]
        [InlineData("xml")]
        [InlineData("edifact")]
        [InlineData("json")]
        [InlineData("future-format")]
        [InlineData("")]
        public void Tipo_fora_da_allowlist_nao_e_elegivel(string detectedType)
        {
            var result = LowCodeTransformationEligibility.Evaluate(
                parseSucceeded: true,
                layoutGuid: "LAY_SYNTHETIC",
                rawText: "CONTEUDO SINTETICO",
                detectedType: detectedType,
                isXmlInput: false);

            Assert.False(result.IsEligible);
            Assert.Equal("type_not_positional", result.Reason);
        }
    }
}
