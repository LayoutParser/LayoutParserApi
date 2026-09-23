using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Services.XmlAnalysis
{
    /// <summary>
    /// Cobre o fix de path traversal (CodeQL cs/path-injection) em
    /// <see cref="XsdValidationService.GetOrientationsAsync"/>: <c>xsdVersion</c> vem de
    /// parâmetro de request e antes ia direto para <c>Path.Combine</c> com <c>_pdfBasePath</c>,
    /// sem validação — um valor como <c>../../../Windows</c> escaparia da pasta de PDFs.
    /// </summary>
    public class XsdValidationServiceOrientationPathTests : IDisposable
    {
        private readonly string _pdfBaseDir;

        public XsdValidationServiceOrientationPathTests()
        {
            _pdfBaseDir = Path.Combine(Path.GetTempPath(), "xsd-orientation-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_pdfBaseDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_pdfBaseDir, recursive: true); } catch { /* best-effort */ }
        }

        private XsdValidationService CreateService()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XsdValidation:BasePath"] = _pdfBaseDir,
                    ["XsdValidation:PdfBasePath"] = _pdfBaseDir
                })
                .Build();

            var detector = new XmlDocumentTypeDetector(NullLogger<XmlDocumentTypeDetector>.Instance);
            var pdfReader = new PdfOrientationReader(NullLogger<PdfOrientationReader>.Instance);

            return new XsdValidationService(NullLogger<XsdValidationService>.Instance, config, detector, pdfReader);
        }

        [Fact]
        public async Task GetOrientationsAsync_XsdVersionComPathTraversal_NaoEscapaDaPastaBase()
        {
            // Pasta irmã fora do _pdfBaseDir, que um "../" tentaria alcançar.
            var pastaVizinha = Path.Combine(Path.GetDirectoryName(_pdfBaseDir)!, "outra-pasta-" + Guid.NewGuid());
            Directory.CreateDirectory(pastaVizinha);
            File.WriteAllText(Path.Combine(pastaVizinha, "segredo.txt"), "não deveria ser alcançável");

            try
            {
                var service = CreateService();
                var xsdVersionMalicioso = "../" + Path.GetFileName(pastaVizinha);

                var resultado = await service.GetOrientationsAsync(xsdVersionMalicioso, errorCodes: null);

                // Degrada graciosamente: não lança, devolve o fallback de "pasta não encontrada"
                // (mesmo formato que o caso legítimo de pasta ausente — não revela que o valor
                // foi rejeitado por tentativa de path traversal).
                Assert.False(resultado.Success);
                Assert.Contains(resultado.Orientations, o => o.Contains("Pasta de orientações PDF não encontrada"));
            }
            finally
            {
                try { Directory.Delete(pastaVizinha, recursive: true); } catch { /* best-effort */ }
            }
        }

        [Fact]
        public async Task GetOrientationsAsync_XsdVersionValida_ContinuaFuncionando()
        {
            var versao = "PL_010b_NT2025_002_v1.30";
            Directory.CreateDirectory(Path.Combine(_pdfBaseDir, versao));

            var service = CreateService();
            var resultado = await service.GetOrientationsAsync(versao, errorCodes: null);

            Assert.True(resultado.Success);
            // Sem PDFs na pasta, cai no fallback genérico — mas a pasta válida foi aceita (sem
            // cair no "pasta não encontrada" que indicaria rejeição indevida pelo validador).
            Assert.DoesNotContain(resultado.Orientations, o => o.Contains("Pasta de orientações PDF não encontrada"));
        }
    }
}
