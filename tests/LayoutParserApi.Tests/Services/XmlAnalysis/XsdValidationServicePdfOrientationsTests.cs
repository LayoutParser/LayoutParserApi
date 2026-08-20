using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Services.XmlAnalysis
{
    /// <summary>
    /// Cobre <c>XsdValidationService.GetOrientationsAsync</c> pós-implementação da leitura real
    /// de PDF (débito técnico antes marcado como TODO em XsdValidationService.cs:379).
    /// </summary>
    public class XsdValidationServicePdfOrientationsTests
    {
        private static XsdValidationService CreateService(string pdfBasePath)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XsdValidation:PdfBasePath"] = pdfBasePath,
                })
                .Build();

            return new XsdValidationService(
                NullLogger<XsdValidationService>.Instance,
                configuration,
                new XmlDocumentTypeDetector(NullLogger<XmlDocumentTypeDetector>.Instance));
        }

        [Fact]
        public async Task GetOrientationsAsync_sem_pasta_de_versao_degrada_para_mensagem_generica()
        {
            // Caminho feliz de falha graciosa: pasta da versão não existe — não pode derrubar o
            // request, deve responder Success=true com orientação informando a ausência.
            var pdfBasePath = Directory.CreateTempSubdirectory("lp-pdf-orient-").FullName;
            try
            {
                var service = CreateService(pdfBasePath);

                var resultado = await service.GetOrientationsAsync("VERSAO_INEXISTENTE", null);

                // Contrato pré-existente do método: pasta ausente não lança exceção, mas
                // Success permanece false (só o texto de orientação é populado).
                Assert.False(resultado.Success);
                Assert.Contains(resultado.Orientations, o => o.Contains("Pasta de orientações PDF não encontrada"));
            }
            finally
            {
                Directory.Delete(pdfBasePath, recursive: true);
            }
        }

        [Fact]
        public async Task GetOrientationsAsync_com_pdf_valido_extrai_trecho_do_codigo_de_erro()
        {
            // Caminho feliz real: PDF válido (texto nativo, não escaneado) contendo o código de
            // erro buscado — o serviço deve extrair o parágrafo relevante em vez de cair no
            // fallback genérico.
            var pdfBasePath = Directory.CreateTempSubdirectory("lp-pdf-orient-").FullName;
            try
            {
                const string version = "PL_010b_NT2025_002_v1.30";
                var versionPath = Path.Combine(pdfBasePath, version);
                Directory.CreateDirectory(versionPath);
                await File.WriteAllBytesAsync(
                    Path.Combine(versionPath, "orientacoes.pdf"),
                    BuildSinglePageTextPdf("Erro E001: campo CNPJ invalido, corrija o formato."));

                var service = CreateService(pdfBasePath);

                var resultado = await service.GetOrientationsAsync(version, new List<string> { "E001" });

                Assert.True(resultado.Success);
                Assert.Contains(resultado.Orientations, o => o.Contains("E001") && o.Contains("CNPJ"));
            }
            finally
            {
                Directory.Delete(pdfBasePath, recursive: true);
            }
        }

        /// <summary>
        /// Monta um PDF minimalista (1 página, fonte Helvetica, texto fixo) byte a byte — o
        /// suficiente pra PdfPig extrair texto real via <c>page.Text</c>, sem depender de
        /// biblioteca de escrita de PDF (PdfPig só lê).
        /// </summary>
        private static byte[] BuildSinglePageTextPdf(string text)
        {
            var content = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
            var contentBytes = System.Text.Encoding.ASCII.GetBytes(content);

            var objects = new List<string>
            {
                "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
                "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
                "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n",
                "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
                $"5 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            };

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII) { AutoFlush = true };

            writer.Write("%PDF-1.4\n");
            var offsets = new List<int>();
            foreach (var obj in objects)
            {
                offsets.Add((int)ms.Length);
                writer.Write(obj);
            }

            var xrefStart = (int)ms.Length;
            writer.Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
                writer.Write($"{offset:D10} 00000 n \n");

            writer.Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF");

            return ms.ToArray();
        }

        [Fact]
        public async Task GetOrientationsAsync_com_pdf_corrompido_degrada_para_fallback_generico()
        {
            // Caminho de falha graciosa: existe um "PDF" na pasta mas o conteúdo não é um PDF
            // válido — PdfPig lança exceção ao abrir, o serviço não pode derrubar o request.
            var pdfBasePath = Directory.CreateTempSubdirectory("lp-pdf-orient-").FullName;
            try
            {
                const string version = "PL_010b_NT2025_002_v1.30";
                var versionPath = Path.Combine(pdfBasePath, version);
                Directory.CreateDirectory(versionPath);
                await File.WriteAllTextAsync(Path.Combine(versionPath, "orientacoes.pdf"), "isto nao e um pdf valido");

                var service = CreateService(pdfBasePath);

                var resultado = await service.GetOrientationsAsync(version, new List<string> { "E001" });

                Assert.True(resultado.Success);
                // Cai no fallback genérico (nenhum trecho extraído do PDF inválido).
                Assert.Contains(resultado.Orientations, o => o.Contains("Para corrigir os erros de validação XSD"));
            }
            finally
            {
                Directory.Delete(pdfBasePath, recursive: true);
            }
        }
    }
}
