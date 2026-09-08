using System.Security.Cryptography;
using System.Text;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Calcula o <c>DocumentId</c> estável descrito no ADR de correção guiada por humano
    /// (docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md §4, Gap 1).
    /// Determinístico: mesmo <c>InputContent</c> + mesmo <c>resolvedLayoutGuid</c> sempre produzem
    /// o mesmo id — reenviar o mesmo documento não cria identificadores diferentes.
    /// </summary>
    public static class DocumentIdCalculator
    {
        /// <summary>
        /// <c>DocumentId = "doc_" + SHA256(InputContent + "|" + resolvedLayoutGuid)[:16]</c> (hex minúsculo).
        /// </summary>
        public static string Calculate(string inputContent, string resolvedLayoutGuid)
        {
            var raw = (inputContent ?? string.Empty) + "|" + (resolvedLayoutGuid ?? string.Empty);
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            var hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return "doc_" + hex[..16];
        }
    }
}
