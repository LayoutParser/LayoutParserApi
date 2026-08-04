using LayoutParserApi.Services.Transformation.LowCode;

namespace LayoutParserApi.Tests.Transformation
{
    public class LowCodeTransformationEligibilityTests
    {
        [Theory]
        [InlineData("mqseries")]
        [InlineData("idoc")]
        [InlineData("unknown")]
        public void Entrada_nao_xml_parseada_elegivel_independentemente_do_tipo_detectado(string detectedType)
        {
            var result = LowCodeTransformationEligibility.Evaluate(
                parseSucceeded: true,
                layoutGuid: "LAY_SYNTHETIC",
                rawText: "CONTEUDO SINTETICO",
                isXmlInput: false);

            Assert.True(result.IsEligible, $"O tipo {detectedType} não deve participar do gate.");
            Assert.Null(result.Reason);
        }

        [Fact]
        public void Xml_nao_e_elegivel()
        {
            var result = LowCodeTransformationEligibility.Evaluate(
                parseSucceeded: true,
                layoutGuid: "LAY_SYNTHETIC",
                rawText: "<root />",
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
                isXmlInput: false);

            Assert.False(result.IsEligible);
            Assert.Equal("no_mapper", result.Reason);
        }
    }
}
