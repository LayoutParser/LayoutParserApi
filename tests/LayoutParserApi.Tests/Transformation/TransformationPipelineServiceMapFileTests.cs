using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Regressão da issue #39: <c>TransformationPipelineService.LoadMappingFileAsync</c> procurava o
    /// "MAP" (estrutura &lt;MAP&gt;&lt;LINE&gt;&lt;FIELD/&gt;&lt;/LINE&gt;&lt;/MAP&gt;) em
    /// <c>MappingPath/MAP_{layoutName}.xml</c> (Mapeamentro) — pasta que não existe em produção — o que
    /// fazia o pathway tcl-xsl nunca resolver candidato para nenhum layout real.
    ///
    /// <para>Evidência de produção (dump <c>.claude/tmp/servidor/layoutparser/</c>, 2026-08-12): o arquivo
    /// com a definição de LINE/FIELD para <c>LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe</c> (caso real do
    /// dump — mapper <c>MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE</c>) fica em
    /// <c>tcl/LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.tcl</c> — nomeado pelo LAYOUT, não pelo mapper, e
    /// com extensão .tcl apesar do conteúdo ser XML puro. Este teste fixa essa convenção usando um
    /// fixture minimal (mesma estrutura raiz &lt;MAP&gt;&lt;LINE&gt;&lt;FIELD/&gt;&lt;/LINE&gt;&lt;/MAP&gt;
    /// confirmada byte-a-byte contra o arquivo real).
    /// </para>
    /// </summary>
    public class TransformationPipelineServiceMapFileTests
    {
        private const string LayoutName = "LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe";

        [Fact]
        public async Task Layout_real_CNHI_resolve_MAP_via_TclPath_layoutName_tcl()
        {
            var tclDir = Directory.CreateTempSubdirectory("lp-tcl-").FullName;
            var xslDir = Directory.CreateTempSubdirectory("lp-xsl-").FullName;
            try
            {
                // Fixture minimal, mesma estrutura raiz do arquivo real de produção
                // (tcl/LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.tcl): extensão .tcl, conteúdo XML <MAP>.
                var mapXml =
                    "<MAP>" +
                    "  <LINE identifier=\"HEADER\" name=\"HEADER\">" +
                    "    <FIELD name=\"data\" length=\"8\"/>" +
                    "  </LINE>" +
                    "</MAP>";
                await File.WriteAllTextAsync(Path.Combine(tclDir, $"{LayoutName}.tcl"), mapXml);

                // XSL identidade só para permitir o pipeline concluir a etapa 2 — não é o foco da
                // regressão (a convenção real de nome de XSL é um problema separado, fora do escopo
                // desta issue).
                var xslContent =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
                    "  <xsl:output method=\"xml\" encoding=\"UTF-8\"/>" +
                    "  <xsl:template match=\"/\"><Resultado/></xsl:template>" +
                    "</xsl:stylesheet>";
                await File.WriteAllTextAsync(Path.Combine(xslDir, $"{LayoutName}_identidade.xsl"), xslContent);

                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TransformationPipeline:TclPath"] = tclDir,
                        ["TransformationPipeline:XslPath"] = xslDir,
                    })
                    .Build();

                var service = new TransformationPipelineService(
                    NullLogger<TransformationPipelineService>.Instance,
                    configuration);

                var txtContent = "20260812083000EMPRESA TESTE       ";

                var resultado = await service.TransformTxtToXmlAsync(txtContent, LayoutName);

                Assert.True(resultado.Success, string.Join("; ", resultado.Errors));
                Assert.Equal(Path.Combine(tclDir, $"{LayoutName}.tcl"), resultado.TclPath);
                Assert.NotNull(resultado.XslPath);
                Assert.NotNull(resultado.TransformedXml);
            }
            finally
            {
                Directory.Delete(tclDir, recursive: true);
                Directory.Delete(xslDir, recursive: true);
            }
        }

        [Fact]
        public async Task Sem_arquivo_tcl_para_o_layout_MAP_nao_e_encontrado()
        {
            var tclDir = Directory.CreateTempSubdirectory("lp-tcl-vazio-").FullName;
            var xslDir = Directory.CreateTempSubdirectory("lp-xsl-vazio-").FullName;
            try
            {
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TransformationPipeline:TclPath"] = tclDir,
                        ["TransformationPipeline:XslPath"] = xslDir,
                    })
                    .Build();

                var service = new TransformationPipelineService(
                    NullLogger<TransformationPipelineService>.Instance,
                    configuration);

                var resultado = await service.TransformTxtToXmlAsync("qualquer conteudo", "LAY_INEXISTENTE");

                Assert.False(resultado.Success);
                Assert.Contains(resultado.Errors, e => e.Contains("Arquivo MAP não encontrado"));
            }
            finally
            {
                Directory.Delete(tclDir, recursive: true);
                Directory.Delete(xslDir, recursive: true);
            }
        }
    }
}
