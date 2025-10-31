using System.Diagnostics;
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
        private readonly string _layoutParserDecrypt;

        public DecryptionService(ILogger<DecryptionService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _layoutParserDecrypt = configuration["LayoutParserDecrypt:Path"] ?? @"C:\Users\elson.lopes\source\repos\LayoutParserDecrypt\bin\Debug\LayoutParserDecrypt.exe";
        }

        public string DecryptContent(string encryptedContent)
        {
            string tempInputFile = null;
            string tempOutputFile = null;

            try
            {
                if (string.IsNullOrEmpty(encryptedContent))
                {
                    _logger.LogWarning("Conteúdo criptografado está vazio");
                    return string.Empty;
                }

                tempInputFile = Path.GetTempFileName();
                tempOutputFile = Path.GetTempFileName();

                File.WriteAllText(tempInputFile, encryptedContent, Encoding.UTF8);

                _logger.LogInformation("Descriptografando conteúdo usando CryptographySysMiddle.Cryptography (tamanho: {Size} caracteres)", encryptedContent.Length);

                CallLegacyDecryptor(tempInputFile, tempOutputFile);

                string decryptedContent = File.ReadAllText(tempOutputFile, Encoding.UTF8);

                return decryptedContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao descriptografar conteúdo");
                _logger.LogWarning("Tentando usar conteúdo sem descriptografia");
                return encryptedContent;
            }
        }

        private void CallLegacyDecryptor(string inputFile, string outputFile)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = _layoutParserDecrypt,
                Arguments = $"\"{inputFile}\" \"{outputFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = new Process();
            process.StartInfo = processStartInfo;

            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            bool exited = process.WaitForExit(30000);

            if (!exited)
            {
                process.Kill();
                throw new Exception("Legacy decryptor timeout (30 segundos)");
            }

            if (process.ExitCode != 0)
            {
                throw new Exception($"Legacy decryptor failed (Exit code: {process.ExitCode}): {error}");
            }

            _logger.LogDebug("Processo legado finalizado: {Output}", output);
        }
    }
}
