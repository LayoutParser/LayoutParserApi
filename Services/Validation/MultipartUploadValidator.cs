using System.IO.Compression;
using System.Text;
using System.Xml;

using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Validation
{
    /// <summary>Resultado da validação de um artefato enviado por upload.</summary>
    public sealed record UploadValidationResult(bool IsValid, string? Error, string MimeSniffed);

    /// <summary>
    /// Validação de segurança de upload multipart (Slice 2 — issue #229, spec §13). Greenfield: não há
    /// precedente de upload na API (ver design-slice2-fiscalmappingpackage-2026-08-31.md §1). Unit
    /// testável isolado do controller — nenhuma dependência de <c>HttpContext</c>.
    /// </summary>
    /// <remarks>
    /// Ordem de validação (deliberada, cada etapa é fail-closed e para na primeira falha):
    /// 1. Tamanho. 2. Extensão declarada por <see cref="ArtifactKind"/>. 3. MIME real (magic bytes).
    /// 4. Defesa XXE (XML) / guarda de zip bomb (XLSX). Hash SHA256 é calculado DEPOIS, pelo chamador,
    /// só quando a validação passa inteira.
    /// </remarks>
    public sealed class MultipartUploadValidator
    {
        /// <summary>Limite por artefato — 50MB. Escolha documentada: cobre planilhas spec e XMLs
        /// gabarito reais do domínio fiscal com folga, sem permitir upload de blob arbitrário grande.</summary>
        public const long MaxArtifactSizeBytes = 50L * 1024 * 1024;

        /// <summary>Guarda de zip bomb: razão máxima descomprimido/comprimido tolerada por entrada.</summary>
        private const double MaxZipCompressionRatio = 100.0;

        /// <summary>Guarda de zip bomb: tamanho descomprimido total máximo projetado (soma das entradas).</summary>
        private const long MaxZipUncompressedTotalBytes = 200L * 1024 * 1024;

        private static readonly Dictionary<string, string> ExpectedExtensionByKind = new(StringComparer.OrdinalIgnoreCase)
        {
            [ArtifactKind.Sample] = ".txt",
            [ArtifactKind.Layout] = ".xml",
            [ArtifactKind.Spec] = ".xlsx",
            [ArtifactKind.Xsd] = ".xsd",
            [ArtifactKind.ExpectedXml] = ".xml",
            [ArtifactKind.FiscalContext] = ".json",
        };

        /// <summary>
        /// Valida um artefato completo (já em memória — o chamador é responsável por não bufferizar
        /// além de <see cref="MaxArtifactSizeBytes"/> antes de chegar aqui). Nunca lança para entrada
        /// maliciosa — sempre retorna <see cref="UploadValidationResult"/> com <c>IsValid=false</c>.
        /// </summary>
        public UploadValidationResult Validate(byte[] content, string originalFileName, string kind)
        {
            // 1. Tamanho.
            if (content.Length == 0)
                return new UploadValidationResult(false, "Artefato vazio.", "unknown");

            if (content.Length > MaxArtifactSizeBytes)
                return new UploadValidationResult(false, $"Artefato excede o limite de {MaxArtifactSizeBytes} bytes.", "unknown");

            // 2. Extensão declarada por Kind (allowlist).
            if (!ExpectedExtensionByKind.TryGetValue(kind, out var expectedExtension))
                return new UploadValidationResult(false, $"Kind de artefato desconhecido: {kind}.", "unknown");

            var actualExtension = Path.GetExtension(originalFileName);
            if (!string.Equals(actualExtension, expectedExtension, StringComparison.OrdinalIgnoreCase))
                return new UploadValidationResult(false, $"Extensão \"{actualExtension}\" não corresponde ao esperado \"{expectedExtension}\" para o tipo {kind}.", "unknown");

            // 3. MIME real via magic bytes — nunca confiar em IFormFile.ContentType.
            var sniffed = SniffMime(content);
            var expectedSniff = ExpectedSniffByExtension(expectedExtension);
            if (!string.Equals(sniffed, expectedSniff, StringComparison.OrdinalIgnoreCase))
                return new UploadValidationResult(false, $"MIME real (\"{sniffed}\") diverge do esperado (\"{expectedSniff}\") para o tipo {kind}.", sniffed);

            // 4. Defesas específicas por tipo real sniffado.
            if (sniffed == "xml")
            {
                var xmlError = ValidateXmlIsXxeSafe(content);
                if (xmlError != null)
                    return new UploadValidationResult(false, xmlError, sniffed);
            }
            else if (sniffed == "zip")
            {
                var zipError = ValidateZipIsNotABomb(content);
                if (zipError != null)
                    return new UploadValidationResult(false, zipError, sniffed);
            }
            else if (sniffed == "json")
            {
                if (!TryParseJsonSafely(content))
                    return new UploadValidationResult(false, "JSON malformado.", sniffed);
            }
            else if (sniffed == "text")
            {
                if (!LooksLikeText(content))
                    return new UploadValidationResult(false, "Conteúdo não parece texto plano (binário inesperado).", sniffed);
            }

            return new UploadValidationResult(true, null, sniffed);
        }

        /// <summary>Assinatura binária real — só os poucos formatos aceitos neste slice.</summary>
        private static string SniffMime(byte[] content)
        {
            // ZIP (OOXML/XLSX): PK\x03\x04.
            if (content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B && content[2] == 0x03 && content[3] == 0x04)
                return "zip";

            // BOM UTF-8 + "<?xml" ou "<?xml" direto (XML/XSD).
            var span = content.AsSpan(0, Math.Min(content.Length, 64));
            var text = Encoding.UTF8.GetString(span).TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<", StringComparison.Ordinal))
                return "xml";

            if (text.StartsWith("{") || text.StartsWith("["))
                return "json";

            // TXT/CSV: sem assinatura fixa — heurística de encoding aplicada em LooksLikeText.
            return "text";
        }

        private static string ExpectedSniffByExtension(string extension) => extension.ToLowerInvariant() switch
        {
            ".xlsx" => "zip",
            ".xml" or ".xsd" => "xml",
            ".json" => "json",
            _ => "text",
        };

        /// <summary>
        /// Defesa XXE clássica: <c>XmlResolver=null</c> + <c>DtdProcessing=Prohibit</c>. Se o XML
        /// declarar DOCTYPE/entidade externa, o parser lança — tratado aqui como rejeição, nunca
        /// deixa a exceção subir com conteúdo do arquivo anexado.
        /// </summary>
        private static string? ValidateXmlIsXxeSafe(byte[] content)
        {
            var settings = new XmlReaderSettings
            {
                XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersFromEntities = 1024,
            };

            using var stream = new MemoryStream(content);
            try
            {
                using var reader = XmlReader.Create(stream, settings);
                while (reader.Read())
                {
                    // Só percorre a árvore para forçar o parser a processar (e rejeitar) DTD/entidade
                    // externa — não precisamos do conteúdo aqui, só validar que é seguro.
                }
                return null;
            }
            catch (XmlException ex)
            {
                // Mensagem da exceção pode ecoar um trecho do XML — não repassar ao cliente/log bruto.
                return $"XML inválido ou inseguro (DTD/entidade externa proibida): {ex.GetType().Name}.";
            }
        }

        /// <summary>
        /// Guarda de zip bomb (XLSX é OOXML = ZIP): rejeita antes de qualquer extração de conteúdo se
        /// a razão de compressão de qualquer entrada, ou o total descomprimido projetado, for suspeito.
        /// </summary>
        private static string? ValidateZipIsNotABomb(byte[] content)
        {
            using var stream = new MemoryStream(content);
            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                long totalUncompressed = 0;

                foreach (var entry in archive.Entries)
                {
                    totalUncompressed += entry.Length;

                    if (entry.CompressedLength > 0)
                    {
                        var ratio = (double)entry.Length / entry.CompressedLength;
                        if (ratio > MaxZipCompressionRatio)
                            return $"Entrada \"{entry.Name}\" tem razão de compressão suspeita ({ratio:F0}:1) — rejeitado como possível zip bomb.";
                    }
                    else if (entry.Length > 0)
                    {
                        // CompressedLength=0 com Length>0 é, em si, suspeito.
                        return $"Entrada \"{entry.Name}\" tem tamanho comprimido zero com conteúdo — rejeitado.";
                    }

                    if (totalUncompressed > MaxZipUncompressedTotalBytes)
                        return "Tamanho descomprimido projetado do XLSX excede o limite — rejeitado como possível zip bomb.";
                }

                return null;
            }
            catch (InvalidDataException)
            {
                return "Arquivo ZIP/XLSX corrompido ou inválido.";
            }
        }

        private static bool TryParseJsonSafely(byte[] content)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(content);
                return true;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>Heurística simples: texto plano não deve conter bytes nulos nem excesso de bytes de controle.</summary>
        private static bool LooksLikeText(byte[] content)
        {
            var sampleLength = Math.Min(content.Length, 8192);
            var controlCount = 0;
            for (var i = 0; i < sampleLength; i++)
            {
                var b = content[i];
                if (b == 0)
                    return false;

                if (b < 0x09 || (b > 0x0D && b < 0x20))
                    controlCount++;
            }

            return controlCount < sampleLength * 0.05;
        }
    }
}
