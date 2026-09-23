using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Services.XmlAnalysis
{
    /// <summary>
    /// Cobre o critério de aceite da issue #172: pasta com PDF válido, pasta sem PDF, PDF
    /// corrompido — cada cenário precisa degradar graciosamente, sem lançar para o chamador.
    /// </summary>
    public class PdfOrientationReaderTests : IDisposable
    {
        private readonly string _tempDir;

        public PdfOrientationReaderTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "pdf-orientation-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }

        private static PdfOrientationReader CreateReader() => new(NullLogger<PdfOrientationReader>.Instance);

        [Fact]
        public void ReadOrientations_PastaSemPdf_DevolveResultadoVazioSemLancar()
        {
            var reader = CreateReader();

            var resultado = reader.ReadOrientations(_tempDir, errorCodes: new List<string> { "ERRO123" });

            Assert.False(resultado.Success);
            Assert.Empty(resultado.Trechos);
        }

        [Fact]
        public void ReadOrientations_PastaInexistente_DevolveResultadoVazioSemLancar()
        {
            var reader = CreateReader();

            var resultado = reader.ReadOrientations(Path.Combine(_tempDir, "nao-existe"), errorCodes: null);

            Assert.False(resultado.Success);
            Assert.Empty(resultado.Trechos);
        }

        [Fact]
        public void ReadOrientations_PdfCorrompido_DegradaGraciosamenteSemLancar()
        {
            var caminhoPdf = Path.Combine(_tempDir, "corrompido.pdf");
            File.WriteAllText(caminhoPdf, "isto nao e um PDF valido");
            var reader = CreateReader();

            var resultado = reader.ReadOrientations(_tempDir, errorCodes: new List<string> { "ERRO123" });

            Assert.False(resultado.Success);
            Assert.Empty(resultado.Trechos);
        }

        [Fact]
        public void ReadOrientations_PdfValidoComErroCorrespondente_ExtraiTrechoDeContexto()
        {
            var caminhoPdf = Path.Combine(_tempDir, "orientacao.pdf");
            File.WriteAllBytes(caminhoPdf, MinimalPdfComTexto("Erro ERRO123: campo obrigatorio ausente no grupo N01."));
            var reader = CreateReader();

            var resultado = reader.ReadOrientations(_tempDir, errorCodes: new List<string> { "ERRO123" });

            Assert.True(resultado.Success);
            Assert.Single(resultado.Trechos);
            Assert.Contains("ERRO123", resultado.Trechos[0].Texto);
            Assert.Equal("orientacao.pdf", resultado.Trechos[0].Arquivo);
        }

        [Fact]
        public void ReadOrientations_SemErrosEspecificos_DevolveResumoInicial()
        {
            var caminhoPdf = Path.Combine(_tempDir, "orientacao.pdf");
            File.WriteAllBytes(caminhoPdf, MinimalPdfComTexto("Manual de orientacoes gerais da SEFAZ."));
            var reader = CreateReader();

            var resultado = reader.ReadOrientations(_tempDir, errorCodes: null);

            Assert.True(resultado.Success);
            Assert.Single(resultado.Trechos);
        }

        /// <summary>
        /// Monta um PDF de página única, minimalista, com o texto informado desenhado via
        /// operador <c>Tj</c> (fonte padrão Helvetica) — suficiente para o PdfPig extrair via
        /// <c>page.Text</c> sem precisar de nenhuma dependência de geração de PDF no projeto.
        /// </summary>
        private static byte[] MinimalPdfComTexto(string texto)
        {
            var conteudo = $"BT /F1 12 Tf 20 700 Td ({EscapePdfString(texto)}) Tj ET";
            var conteudoBytes = System.Text.Encoding.ASCII.GetBytes(conteudo);

            var objetos = new List<string>
            {
                "1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj\n",
                "2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj\n",
                "3 0 obj<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> >> /MediaBox [0 0 612 792] /Contents 4 0 R >>endobj\n",
                $"4 0 obj<< /Length {conteudoBytes.Length} >>stream\n{conteudo}\nendstream\nendobj\n",
                "5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj\n"
            };

            var sb = new System.Text.StringBuilder();
            sb.Append("%PDF-1.4\n");
            var offsets = new List<int>();
            foreach (var objeto in objetos)
            {
                offsets.Add(System.Text.Encoding.ASCII.GetByteCount(sb.ToString()));
                sb.Append(objeto);
            }

            var xrefStart = System.Text.Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"xref\n0 {objetos.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
                sb.Append($"{offset:D10} 00000 n \n");
            sb.Append($"trailer<< /Size {objetos.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF");

            return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static string EscapePdfString(string texto)
            => texto.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }
}
