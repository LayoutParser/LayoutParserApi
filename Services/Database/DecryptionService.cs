using System.Security.Cryptography;
using System.Text;

namespace LayoutParserApi.Services.Database
{
    public interface IDecryptionService
    {
        string DecryptContent(string encryptedContent);
    }

    public class DecryptionService : IDecryptionService
    {
        private readonly ILogger<DecryptionService> _logger;

        public DecryptionService(ILogger<DecryptionService> logger)
        {
            _logger = logger;
        }

        public string DecryptContent(string encryptedContent)
        {
            try
            {
                if (string.IsNullOrEmpty(encryptedContent))
                {
                    _logger.LogWarning("Conteúdo criptografado está vazio");
                    return string.Empty;
                }

                _logger.LogInformation("Descriptografando conteúdo usando CryptographySysMiddle.Cryptography (tamanho: {Size} caracteres)", encryptedContent.Length);

                // Sempre remover prefixo de 3 caracteres
                if (encryptedContent.Length <= 3)
                    throw new FormatException("Conteúdo muito curto para remoção de prefixo de 3 caracteres.");
                var withoutPrefix = encryptedContent.Substring(3);

                var decrypted = CryptographySysMiddle.Cryptography.Decrypt(withoutPrefix);
                return decrypted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao descriptografar conteúdo");
                _logger.LogWarning("Tentando usar conteúdo sem descriptografia");
                return encryptedContent;
            }
        }
    }
}
