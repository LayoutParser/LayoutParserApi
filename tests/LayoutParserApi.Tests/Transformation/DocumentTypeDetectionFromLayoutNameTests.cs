using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Débito técnico: <c>AutomatedTransformationTestService</c> e <c>TransformationValidatorService</c>
    /// hardcodavam "NFe" ao chamar <c>TransformTxtToXmlAsync</c> (TODO explícito nos dois pontos).
    /// A correção reaproveita <see cref="XmlDocumentTypeDetector.DetectFromLayoutName"/> — já existente
    /// e já registrado no DI, mas não usado nesses dois call sites — para detectar o tipo real a partir
    /// do nome do layout (único dado disponível ali antes da transformação rodar; o namespace do XML só
    /// existe DEPOIS). Estes testes cobrem o detector em si: é a peça nova de lógica que os dois serviços
    /// passam a consumir, com fallback explícito para "NFe" quando o layout não indica o tipo.
    /// </summary>
    public class DocumentTypeDetectionFromLayoutNameTests
    {
        private static XmlDocumentTypeDetector CreateDetector() =>
            new XmlDocumentTypeDetector(NullLogger<XmlDocumentTypeDetector>.Instance);

        [Theory]
        [InlineData("LAY_CNHI_TXT_MQSERIES_ENVCTE_3.00_CTe", "CTE")]
        [InlineData("LAY_FIAT_TXT_MQSERIES_ENVMDFE_3.00_MDFe", "MDFe")]
        [InlineData("LAY_ACME_TXT_ENVNFCOM_1.00", "NFCom")]
        [InlineData("LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe", "NFe")]
        public void DetectFromLayoutName_reconhece_tipo_pelo_nome_do_layout(string layoutName, string expectedType)
        {
            var detector = CreateDetector();

            var result = detector.DetectFromLayoutName(layoutName);

            Assert.Equal(expectedType, result.Type);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("LAY_ACME_LAYOUT_SEM_INDICADOR_DE_TIPO")]
        public void DetectFromLayoutName_retorna_UNKNOWN_quando_nao_ha_indicador_no_nome(string layoutName)
        {
            var detector = CreateDetector();

            var result = detector.DetectFromLayoutName(layoutName);

            // UNKNOWN é o sinal que os serviços chamadores usam para cair no fallback "NFe" com warning
            // explícito, em vez de assumir silenciosamente (comportamento antigo, hardcoded).
            Assert.Equal("UNKNOWN", result.Type);
        }
    }
}
