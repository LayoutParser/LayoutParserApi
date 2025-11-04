using System.Diagnostics;
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
        private readonly string _layoutParserDecryptPath;

        public DecryptionService(ILogger<DecryptionService> logger, IConfiguration configuration)
        {
            _logger = logger;
            
            // IMPORTANTE: A descriptografia só funciona no .NET Framework 4.8.1
            // Devido à tecnologia de criptografia utilizada, é necessário usar o executável
            var configuredPath = configuration["LayoutParserDecrypt:Path"];
            
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                // Tentar encontrar o executável em caminhos relativos comuns
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var possiblePaths = new[]
                {
                    Path.Combine(baseDirectory, "LayoutParserDecrypt.exe"),
                    Path.Combine(baseDirectory, "tools", "LayoutParserDecrypt.exe"),
                    Path.Combine(baseDirectory, "..", "LayoutParserDecrypt", "bin", "Release", "LayoutParserDecrypt.exe"),
                    Path.Combine(baseDirectory, "..", "LayoutParserDecrypt", "bin", "Debug", "LayoutParserDecrypt.exe")
                };

                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        _layoutParserDecryptPath = Path.GetFullPath(path);
                        _logger.LogInformation("Executável LayoutParserDecrypt encontrado em: {Path}", _layoutParserDecryptPath);
                        break;
                    }
                }

                if (string.IsNullOrEmpty(_layoutParserDecryptPath))
                {
                    _logger.LogWarning("LayoutParserDecrypt.exe não encontrado. Configure o caminho em 'LayoutParserDecrypt:Path'");
                }
            }
            else
            {
                _layoutParserDecryptPath = configuredPath;
            }
        }

        public string DecryptContent(string encryptedContent)
        {
            if (string.IsNullOrEmpty(encryptedContent))
            {
                _logger.LogWarning("Conteúdo criptografado está vazio");
                return string.Empty;
            }

            // A descriptografia DEVE ser feita pelo executável .NET Framework 4.8.1
            if (string.IsNullOrEmpty(_layoutParserDecryptPath) || !File.Exists(_layoutParserDecryptPath))
            {
                _logger.LogError("LayoutParserDecrypt.exe não encontrado em: {Path}. Configure o caminho correto em appsettings.json", _layoutParserDecryptPath);
                return encryptedContent;
            }

            return DecryptUsingExecutable(encryptedContent);
        }

        private string DecryptUsingExecutable(string encryptedContent)
        {
            string tempInputFile = null;
            string tempOutputFile = null;

            try
            {
                tempInputFile = Path.GetTempFileName();
                tempOutputFile = Path.GetTempFileName();

                File.WriteAllText(tempInputFile, encryptedContent, Encoding.UTF8);

                _logger.LogInformation("Descriptografando conteúdo usando executável externo (tamanho: {Size} caracteres)", encryptedContent.Length);

                CallLegacyDecryptor(tempInputFile, tempOutputFile);

                string decryptedContent = File.ReadAllText(tempOutputFile, Encoding.UTF8);

                return decryptedContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao descriptografar usando executável");
                return encryptedContent;
            }
            finally
            {
                // Limpar arquivos temporários
                try
                {
                    if (tempInputFile != null && File.Exists(tempInputFile))
                        File.Delete(tempInputFile);
                    if (tempOutputFile != null && File.Exists(tempOutputFile))
                        File.Delete(tempOutputFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao limpar arquivos temporários");
                }
            }
        }

        private void CallLegacyDecryptor(string inputFile, string outputFile)
        {
            if (string.IsNullOrEmpty(_layoutParserDecryptPath) || !File.Exists(_layoutParserDecryptPath))
            {
                throw new FileNotFoundException($"Executável de descriptografia não encontrado: {_layoutParserDecryptPath}");
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _layoutParserDecryptPath,
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
