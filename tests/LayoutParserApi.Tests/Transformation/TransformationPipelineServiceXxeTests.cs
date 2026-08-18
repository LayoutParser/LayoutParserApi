using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Regressão dos alertas CodeQL #4/#5 (medium, "Missing XML validation" / XXE):
    /// <c>ApplyXsltTransformAsync</c> (chamado por <c>TransformXmlToXmlAsync</c>) e
    /// <c>XsdValidationService</c> liam XML de entrada com <see cref="System.Xml.XmlReader"/> sem
    /// desabilitar explicitamente <c>DtdProcessing</c>/<c>XmlResolver</c>. Mesmo com defaults seguros
    /// do .NET moderno, o fix adiciona <c>DtdProcessing = Prohibit</c> e <c>XmlResolver = null</c>
    /// explícitos nos dois pontos. Este teste fixa que um XML malicioso com DOCTYPE/ENTITY externa
    /// não é expandido — o parser rejeita o DTD em vez de resolver a entidade.
    /// </summary>
    public class TransformationPipelineServiceXxeTests
    {
        [Fact]
        public async Task TransformXmlToXmlAsync_com_DOCTYPE_e_entidade_externa_nao_expande_XXE()
        {
            var xslDir = Directory.CreateTempSubdirectory("lp-xsl-xxe-").FullName;
            try
            {
                // XSL trivial só pra permitir a transformação chegar até o parse do XML de entrada.
                var xslContent =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
                    "  <xsl:output method=\"xml\" encoding=\"UTF-8\"/>" +
                    "  <xsl:template match=\"/\"><Resultado/></xsl:template>" +
                    "</xsl:stylesheet>";
                const string layoutName = "LAY_TESTE_XXE";
                await File.WriteAllTextAsync(Path.Combine(xslDir, $"Origem_{layoutName}.xsl"), xslContent);

                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TransformationPipeline:XslPath"] = xslDir,
                    })
                    .Build();

                var service = new TransformationPipelineService(
                    NullLogger<TransformationPipelineService>.Instance,
                    configuration);

                // XML malicioso clássico de XXE: DOCTYPE com ENTITY externa apontando pro filesystem local.
                var xmlMalicioso =
                    "<?xml version=\"1.0\"?>" +
                    "<!DOCTYPE root [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
                    "<root>&xxe;</root>";

                var resultado = await service.TransformXmlToXmlAsync(xmlMalicioso, "Origem", "Destino", layoutName);

                // Com DtdProcessing.Prohibit, o XmlReader lança XmlException ao encontrar o DOCTYPE —
                // a etapa de transformação falha (Success = false) em vez de expandir a entidade externa
                // e vazar conteúdo de arquivo local no XML transformado.
                Assert.False(resultado.Success);
                Assert.Contains(resultado.Errors, e => e.Contains("XSLT", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(resultado.StepResults, kv => kv.Value != null && kv.Value.Contains("root:"));
            }
            finally
            {
                Directory.Delete(xslDir, recursive: true);
            }
        }
    }
}
