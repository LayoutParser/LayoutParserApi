using System.IO.Compression;
using System.Text;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Validation;

namespace LayoutParserApi.Tests.Services.Validation
{
    /// <summary>
    /// Slice 2 (issue #229) — gates de segurança de upload obrigatórios (spec §13). Cada teste aqui é
    /// o oráculo de UMA defesa específica: se a defesa correspondente for removida/enfraquecida no
    /// <see cref="MultipartUploadValidator"/>, o teste fica vermelho.
    /// </summary>
    public class MultipartUploadValidatorTests
    {
        private readonly MultipartUploadValidator _validator = new();

        private static byte[] RealXmlBytes(string root = "<root/>") => Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?>{root}");

        // --- MIME real via magic bytes ---

        [Fact]
        public void MIME_real_diverge_da_extensao_declarada_e_rejeitado()
        {
            // Arquivo .xsd cujo CONTEÚDO REAL não é XML (ex.: um binário qualquer com extensão forjada).
            var spoofed = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 }; // magic bytes de ZIP, não XML

            var result = _validator.Validate(spoofed, "layout.xsd", ArtifactKind.Xsd);

            Assert.False(result.IsValid);
            Assert.Contains("MIME real", result.Error);
        }

        [Fact]
        public void Extensao_declarada_errada_para_o_kind_e_rejeitada_antes_do_sniff()
        {
            var content = RealXmlBytes();

            var result = _validator.Validate(content, "layout.txt", ArtifactKind.Layout);

            Assert.False(result.IsValid);
            Assert.Contains("Extensão", result.Error);
        }

        [Fact]
        public void Xml_valido_com_extensao_correta_e_aceito()
        {
            var content = RealXmlBytes("<Layout><Field/></Layout>");

            var result = _validator.Validate(content, "layout.xml", ArtifactKind.Layout);

            Assert.True(result.IsValid);
            Assert.Equal("xml", result.MimeSniffed);
        }

        // --- limite de tamanho ---

        [Fact]
        public void Artefato_acima_do_limite_e_rejeitado()
        {
            var oversized = new byte[MultipartUploadValidator.MaxArtifactSizeBytes + 1];

            var result = _validator.Validate(oversized, "sample.txt", ArtifactKind.Sample);

            Assert.False(result.IsValid);
            Assert.Contains("excede o limite", result.Error);
        }

        [Fact]
        public void Artefato_vazio_e_rejeitado()
        {
            var result = _validator.Validate(Array.Empty<byte>(), "sample.txt", ArtifactKind.Sample);

            Assert.False(result.IsValid);
        }

        // --- defesa XXE ---

        [Fact]
        public void Xml_com_tentativa_de_xxe_nao_consegue_ler_arquivo_do_sistema()
        {
            // DOCTYPE com entidade externa apontando para um arquivo local — o clássico XXE de
            // exfiltração de arquivo. XmlResolver=null + DtdProcessing=Prohibit deve rejeitar antes
            // de qualquer tentativa de resolver a entidade.
            var maliciousXml = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\"?>" +
                "<!DOCTYPE root [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                "<root>&xxe;</root>");

            var result = _validator.Validate(maliciousXml, "layout.xml", ArtifactKind.Layout);

            Assert.False(result.IsValid);
            Assert.Contains("DTD", result.Error);
        }

        [Fact]
        public void Xml_com_tentativa_de_xxe_ssrf_e_rejeitado()
        {
            var ssrfXml = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\"?>" +
                "<!DOCTYPE root [<!ENTITY xxe SYSTEM \"http://169.254.169.254/latest/meta-data/\">]>" +
                "<root>&xxe;</root>");

            var result = _validator.Validate(ssrfXml, "expected.xml", ArtifactKind.ExpectedXml);

            Assert.False(result.IsValid);
        }

        // --- guarda de zip bomb (XLSX) ---

        [Fact]
        public void Xlsx_com_taxa_de_compressao_suspeita_e_rejeitado_antes_de_extrair()
        {
            var bomb = BuildZipBombBytes();

            var result = _validator.Validate(bomb, "spec.xlsx", ArtifactKind.Spec);

            Assert.False(result.IsValid);
            Assert.Contains("zip bomb", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Xlsx_normal_e_aceito()
        {
            var normal = BuildNormalZipBytes();

            var result = _validator.Validate(normal, "spec.xlsx", ArtifactKind.Spec);

            Assert.True(result.IsValid);
            Assert.Equal("zip", result.MimeSniffed);
        }

        private static byte[] BuildZipBombBytes()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("bomb.xml", CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                // 20MB de zeros comprime para poucos KB — razão >> 100:1.
                var zeros = new byte[1024 * 1024];
                for (var i = 0; i < 20; i++)
                    entryStream.Write(zeros, 0, zeros.Length);
            }

            return stream.ToArray();
        }

        private static byte[] BuildNormalZipBytes()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("sheet.xml", CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes("<worksheet><row><cell>valor</cell></row></worksheet>");
                entryStream.Write(bytes, 0, bytes.Length);
            }

            return stream.ToArray();
        }

        // --- kind desconhecido ---

        [Fact]
        public void Kind_desconhecido_e_rejeitado()
        {
            var result = _validator.Validate(RealXmlBytes(), "algo.xml", "kind-que-nao-existe");

            Assert.False(result.IsValid);
        }
    }
}
