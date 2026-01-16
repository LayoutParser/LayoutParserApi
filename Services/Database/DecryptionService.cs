using LayoutParserApi.Services.Interfaces;

using System.Diagnostics;
using System.Text;
using LayoutParserApi.Services.Logging;

namespace LayoutParserApi.Services.Database
{

    public class DecryptionService : IDecryptionService
    {
        private readonly ILogger<DecryptionService> _logger;
        private readonly string _layoutParserDecryptPath;

        public DecryptionService(ILogger<DecryptionService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _layoutParserDecryptPath = string.Empty; // Inicializar para evitar null
            
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
                    var fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath))
                    {
                        _layoutParserDecryptPath = fullPath;
                        _logger.LogInformation("Executável LayoutParserDecrypt encontrado automaticamente em: {Path}", _layoutParserDecryptPath);
                        break;
                    }
                }

                if (string.IsNullOrEmpty(_layoutParserDecryptPath))
                    _logger.LogWarning("LayoutParserDecrypt.exe não encontrado automaticamente. Configure o caminho em 'LayoutParserDecrypt:Path' no appsettings.json");
                
            }
            else
            {
                _layoutParserDecryptPath = configuredPath;
                if (!File.Exists(_layoutParserDecryptPath))
                    _logger.LogWarning("LayoutParserDecrypt.exe não encontrado no caminho configurado: {Path}", _layoutParserDecryptPath);
                else
                    _logger.LogInformation("Usando LayoutParserDecrypt.exe do caminho configurado: {Path}", _layoutParserDecryptPath);
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
                Arguments = BuildArgs(inputFile, outputFile),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            // ✅ Propagar correlation e log dir para o Decrypt/Lib
            var logDir = Environment.GetEnvironmentVariable("LAYOUTPARSER_LOG_DIR")
                         ?? ""; // definido no Program.cs? fallback vazio
            if (string.IsNullOrWhiteSpace(logDir))
                logDir = ""; // o decrypt fará fallback

            var corr = CorrelationContext.CurrentId ?? Guid.NewGuid().ToString();
            processStartInfo.Environment["LAYOUTPARSER_CORRELATION_ID"] = corr;

            // Preferir o mesmo diretório de logs do API, se configurado
            var configuredLogDir = AppDomain.CurrentDomain.BaseDirectory;
            // tentamos ler do mesmo local do appsettings via variável? manter simples:
            // se existir variável padrão, usar.
            var apiLogDir = Environment.GetEnvironmentVariable("LAYOUTPARSERAPI_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(apiLogDir))
                configuredLogDir = apiLogDir;

            processStartInfo.Environment["LAYOUTPARSER_LOG_DIR"] = string.IsNullOrWhiteSpace(logDir) ? configuredLogDir : logDir;

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
                throw new Exception($"Legacy decryptor failed (Exit code: {process.ExitCode}): {error}");
            

            _logger.LogDebug("Processo legado finalizado: {Output}", output);
        }

        private string BuildArgs(string inputFile, string outputFile)
        {
            // args: input output correlationId logDir
            var corr = CorrelationContext.CurrentId ?? Guid.NewGuid().ToString();
            var logDir = Environment.GetEnvironmentVariable("LAYOUTPARSERAPI_LOG_DIR") ?? "";
            return $"\"{inputFile}\" \"{outputFile}\" \"{corr}\" \"{logDir}\"";
        }
    }
}