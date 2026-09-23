using LayoutParserApi.Services.Fiscal;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Catálogo de exemplos reais TCL/XSL da Neogrid (corpus de referência/oráculo — ver
    /// <see cref="ReferenceExampleCatalogService"/>). Cobre o caminho feliz com fixture sintética
    /// pequena (não o corpus real, que não faz parte do repositório) e a degradação graciosa quando
    /// <c>ReferenceExamples:BasePath</c> está ausente/vazio ou aponta para um diretório inexistente.
    /// </summary>
    public sealed class ReferenceExampleCatalogServiceTests : IDisposable
    {
        private readonly string _tempRoot;

        public ReferenceExampleCatalogServiceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "lp-reference-examples-tests-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        private static IReferenceExampleCatalogService BuildService(string basePath)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("ReferenceExamples:BasePath", basePath)])
                .Build();
            return new ReferenceExampleCatalogService(configuration, NullLogger<ReferenceExampleCatalogService>.Instance);
        }

        private void WriteFixture()
        {
            var tclDir = Path.Combine(_tempRoot, "tcl", "NFe", "4.00");
            var xslDir = Path.Combine(_tempRoot, "xsl", "NFe", "4.00");
            Directory.CreateDirectory(tclDir);
            Directory.CreateDirectory(xslDir);

            // Par completo (tcl + xsl com o mesmo nome-base).
            File.WriteAllText(Path.Combine(tclDir, "NFe009_4.00_EnvioNFe_NeoGridToSefaz.tcl"), "proc envio {} { return 1 }");
            File.WriteAllText(Path.Combine(xslDir, "NFe009_4.00_EnvioNFe_NeoGridToSefaz.xsl"), "<xsl:stylesheet />");

            // Só TCL, sem XSL correspondente — deve aparecer mesmo assim, com XslFileName nulo.
            File.WriteAllText(Path.Combine(tclDir, "NFe009_4.00_InutNFe_NeoGridToSefaz.tcl"), "proc inut {} { return 1 }");

            // Outro DocType, para testar o filtro.
            var cteDir = Path.Combine(_tempRoot, "tcl", "CTe", "3.00");
            Directory.CreateDirectory(cteDir);
            File.WriteAllText(Path.Combine(cteDir, "CTe001_SefazToNeoGrid.tcl"), "proc cte {} { return 1 }");
        }

        [Fact]
        public async Task ListAsync_ComCorpusPresente_RetornaExemplosComParDeArquivosResolvido()
        {
            WriteFixture();
            var service = BuildService(_tempRoot);

            var examples = await service.ListAsync(docType: null, CancellationToken.None);

            Assert.Equal(3, examples.Count);

            var envio = examples.Single(e => e.Scenario == "NFe009_4.00_EnvioNFe");
            Assert.Equal("NFe", envio.DocType);
            Assert.Equal("4.00", envio.Version);
            Assert.Equal("NeoGridToSefaz", envio.Direction);
            Assert.NotNull(envio.TclFileName);
            Assert.NotNull(envio.XslFileName);

            var inut = examples.Single(e => e.Scenario == "NFe009_4.00_InutNFe");
            Assert.NotNull(inut.TclFileName);
            Assert.Null(inut.XslFileName);
        }

        [Fact]
        public async Task ListAsync_FiltradoPorDocType_RetornaSoOsExemplosDoTipo()
        {
            WriteFixture();
            var service = BuildService(_tempRoot);

            var examples = await service.ListAsync(docType: "CTe", CancellationToken.None);

            var example = Assert.Single(examples);
            Assert.Equal("CTe", example.DocType);
        }

        [Fact]
        public async Task GetContentAsync_ComIdValido_RetornaConteudoDosDoisArquivos()
        {
            WriteFixture();
            var service = BuildService(_tempRoot);
            var examples = await service.ListAsync(docType: "NFe", CancellationToken.None);
            var envio = examples.Single(e => e.Scenario == "NFe009_4.00_EnvioNFe");

            var content = await service.GetContentAsync(envio.Id, CancellationToken.None);

            Assert.NotNull(content);
            Assert.Contains("proc envio", content!.TclContent);
            Assert.Contains("xsl:stylesheet", content.XslContent);
        }

        [Fact]
        public async Task GetContentAsync_ComIdInexistente_RetornaNull()
        {
            WriteFixture();
            var service = BuildService(_tempRoot);

            var content = await service.GetContentAsync("id-que-nao-existe", CancellationToken.None);

            Assert.Null(content);
        }

        [Fact]
        public async Task ListAsync_ComBasePathVazio_RetornaListaVaziaSemLancar()
        {
            var service = BuildService(string.Empty);

            var examples = await service.ListAsync(docType: null, CancellationToken.None);

            Assert.Empty(examples);
        }

        [Fact]
        public async Task ListAsync_ComBasePathInexistenteNoDisco_RetornaListaVaziaSemLancar()
        {
            var service = BuildService(Path.Combine(_tempRoot, "nao-existe"));

            var examples = await service.ListAsync(docType: null, CancellationToken.None);

            Assert.Empty(examples);
        }
    }
}
