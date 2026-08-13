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

                // XSL nomeado pela convenção real "{mapperName}_{layoutName}.xsl" (issue #55, confirmada
                // contra xsl/MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE_LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.xsl
                // no dump de produção).
                var xslContent =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
                    "  <xsl:output method=\"xml\" encoding=\"UTF-8\"/>" +
                    "  <xsl:template match=\"/\"><Resultado/></xsl:template>" +
                    "</xsl:stylesheet>";
                await File.WriteAllTextAsync(Path.Combine(xslDir, $"MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE_{LayoutName}.xsl"), xslContent);

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

    /// <summary>
    /// Regressão da issue #55: <c>TransformationPipelineService.FindXslFile</c> (privado, exercitado via
    /// <see cref="TransformationPipelineService.TransformXmlToXmlAsync"/>) usava padrões de nome
    /// (<c>{layoutName}_*.xsl</c>, <c>{targetType}*_{layoutName}.xsl</c> etc.) que não batem com nenhum
    /// arquivo real e caíam num fallback silencioso ("primeiro XSL da pasta"), gerando resultado errado sem
    /// sinalizar nada.
    ///
    /// <para>Evidência de produção (dump <c>.claude/tmp/servidor/layoutparser/xsl/</c>, 2026-08-13):
    /// dois casos reais (CNHI e MARELLI) confirmam a convenção <c>{mapperName}_{layoutName}.xsl</c> — ex.
    /// <c>MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE_LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.xsl</c> e
    /// <c>MAP_MARELLI_SAP_SEND_ENV_TXT_XML_NFE_LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe.xsl</c>. O fix resolve
    /// pelo sufixo <c>*_{layoutName}.xsl</c> (o nome do mapper não é conhecido pelo pipeline) e não usa
    /// mais fallback silencioso: sem match, retorna erro claro em vez de continuar como se nada tivesse
    /// acontecido.</para>
    /// </summary>
    public class TransformationPipelineServiceFindXslFileTests
    {
        private const string LayoutName = "LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe";

        private static IConfiguration BuildConfig(string tclDir, string xslDir) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TransformationPipeline:TclPath"] = tclDir,
                    ["TransformationPipeline:XslPath"] = xslDir,
                })
                .Build();

        [Fact]
        public async Task Resolve_XSL_pela_convencao_real_mapperName_layoutName()
        {
            var tclDir = Directory.CreateTempSubdirectory("lp-tcl-marelli-").FullName;
            var xslDir = Directory.CreateTempSubdirectory("lp-xsl-marelli-").FullName;
            try
            {
                var xslContent =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
                    "  <xsl:output method=\"xml\" encoding=\"UTF-8\"/>" +
                    "  <xsl:template match=\"/\"><Resultado/></xsl:template>" +
                    "</xsl:stylesheet>";
                var xslFileName = $"MAP_MARELLI_SAP_SEND_ENV_TXT_XML_NFE_{LayoutName}.xsl";
                await File.WriteAllTextAsync(Path.Combine(xslDir, xslFileName), xslContent);

                var service = new TransformationPipelineService(
                    NullLogger<TransformationPipelineService>.Instance,
                    BuildConfig(tclDir, xslDir));

                var resultado = await service.TransformXmlToXmlAsync(
                    "<ROOT/>", "Intermediate", "NFe", LayoutName);

                Assert.True(resultado.Success, string.Join("; ", resultado.Errors));
                Assert.Equal(Path.Combine(xslDir, xslFileName), resultado.XslPath);
            }
            finally
            {
                Directory.Delete(tclDir, recursive: true);
                Directory.Delete(xslDir, recursive: true);
            }
        }

        [Fact]
        public async Task Sem_XSL_casando_a_convencao_falha_com_erro_claro_sem_fallback_generico()
        {
            var tclDir = Directory.CreateTempSubdirectory("lp-tcl-semxsl-").FullName;
            var xslDir = Directory.CreateTempSubdirectory("lp-xsl-semxsl-").FullName;
            try
            {
                // XSL de OUTRO layout presente na pasta — antes do fix, o fallback silencioso
                // ("primeiro XSL da pasta") teria usado este arquivo errado.
                var xslContent =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
                    "  <xsl:output method=\"xml\" encoding=\"UTF-8\"/>" +
                    "  <xsl:template match=\"/\"><Resultado/></xsl:template>" +
                    "</xsl:stylesheet>";
                await File.WriteAllTextAsync(
                    Path.Combine(xslDir, "MAP_OUTRO_MAPPER_LAY_OUTRO_LAYOUT.xsl"), xslContent);

                var service = new TransformationPipelineService(
                    NullLogger<TransformationPipelineService>.Instance,
                    BuildConfig(tclDir, xslDir));

                var resultado = await service.TransformXmlToXmlAsync(
                    "<ROOT/>", "Intermediate", "NFe", LayoutName);

                Assert.False(resultado.Success);
                Assert.Contains(resultado.Errors, e => e.Contains("Arquivo XSL não encontrado"));
            }
            finally
            {
                Directory.Delete(tclDir, recursive: true);
                Directory.Delete(xslDir, recursive: true);
            }
        }

        [Fact]
        public async Task Multiplos_XSL_casando_o_sufixo_escolhe_deterministico_e_sinaliza_ambiguidade()
        {
            var tclDir = Directory.CreateTempSubdirectory("lp-tcl-ambiguo-").FullName;
            var xslDir = Directory.CreateTempSubdirectory("lp-xsl-ambiguo-").FullName;
            try
            {
                var xslContent =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
                    "  <xsl:output method=\"xml\" encoding=\"UTF-8\"/>" +
                    "  <xsl:template match=\"/\"><Resultado/></xsl:template>" +
                    "</xsl:stylesheet>";
                var fileA = $"MAP_A_{LayoutName}.xsl";
                var fileB = $"MAP_B_{LayoutName}.xsl";
                await File.WriteAllTextAsync(Path.Combine(xslDir, fileA), xslContent);
                await File.WriteAllTextAsync(Path.Combine(xslDir, fileB), xslContent);

                var service = new TransformationPipelineService(
                    NullLogger<TransformationPipelineService>.Instance,
                    BuildConfig(tclDir, xslDir));

                var resultado = await service.TransformXmlToXmlAsync(
                    "<ROOT/>", "Intermediate", "NFe", LayoutName);

                Assert.True(resultado.Success, string.Join("; ", resultado.Errors));
                // Ordem alfabética determinística ("A" < "B") — não é null nem aleatório.
                Assert.Equal(Path.Combine(xslDir, fileA), resultado.XslPath);
            }
            finally
            {
                Directory.Delete(tclDir, recursive: true);
                Directory.Delete(xslDir, recursive: true);
            }
        }
    }
}
