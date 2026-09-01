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
        private readonly string _logDirFromApi;

        // Timeout do processo externo. Mantido em 30s (comportamento anterior), mas agora ALCANÇÁVEL:
        // antes o ReadToEnd() síncrono bloqueava ANTES do WaitForExit(30000), então o timeout nunca
        // era avaliado e a thread travava para sempre (P1.2 do plano de segurança).
        private const int TimeoutMs = 30000;

        public DecryptionService(ILogger<DecryptionService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _layoutParserDecryptPath = string.Empty; // Inicializar para evitar null
            _logDirFromApi = configuration["Logging:File:Directory"] ?? "";

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

        /// <inheritdoc />
        public bool IsDecryptorAvailable =>
            !string.IsNullOrEmpty(_layoutParserDecryptPath) && File.Exists(_layoutParserDecryptPath);

        /// <inheritdoc />
        public async Task<string> DecryptContentAsync(string encryptedContent)
        {
            if (string.IsNullOrEmpty(encryptedContent))
            {
                _logger.LogWarning("Conteúdo criptografado está vazio");
                return string.Empty;
            }

            // A descriptografia DEVE ser feita pelo executável .NET Framework 4.8.1.
            // ✅ P1.1: se o executável não existe, FALHA EXPLÍCITA — nunca devolve a cifra como se
            // fosse texto claro. O chamador (warm-up / test-decryption) trata a exceção.
            if (!IsDecryptorAvailable)
            {
                _logger.LogError("LayoutParserDecrypt.exe não encontrado em: {Path}. Configure o caminho correto em appsettings.json", _layoutParserDecryptPath);
                throw new DecryptionException(
                    $"Executável de descriptografia não encontrado: '{_layoutParserDecryptPath}'. Nada foi descriptografado.");
            }

            return await DecryptUsingExecutableAsync(encryptedContent);
        }

        private async Task<string> DecryptUsingExecutableAsync(string encryptedContent)
        {
            string? tempInputFile = null;
            string? tempOutputFile = null;

            try
            {
                tempInputFile = Path.GetTempFileName();
                tempOutputFile = Path.GetTempFileName();

                await File.WriteAllTextAsync(tempInputFile, encryptedContent, Encoding.UTF8);

                _logger.LogInformation("Descriptografando conteúdo usando executável externo (tamanho: {Size} caracteres)", encryptedContent.Length);

                await CallLegacyDecryptorAsync(tempInputFile, tempOutputFile);

                return await File.ReadAllTextAsync(tempOutputFile, Encoding.UTF8);
            }
            catch (DecryptionException)
            {
                // Já é a exceção tipada — propaga sem reembrulhar (mantém a mensagem/causa original).
                throw;
            }
            catch (Exception ex)
            {
                // ✅ P1.1: qualquer outra falha (I/O do temp, etc.) também é falha explícita, não
                // "devolve a cifra". Reembrulha como DecryptionException para o chamador distinguir.
                _logger.LogError(ex, "Erro ao descriptografar usando executável");
                throw new DecryptionException("Falha ao descriptografar conteúdo usando executável externo.", ex);
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

        private async Task CallLegacyDecryptorAsync(string inputFile, string outputFile)
        {
            if (!IsDecryptorAvailable)
                throw new DecryptionException($"Executável de descriptografia não encontrado: {_layoutParserDecryptPath}");

            var corr = CorrelationContext.CurrentId ?? Guid.NewGuid().ToString();

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _layoutParserDecryptPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            // ✅ ArgumentList evita reinterpretação de shell (cs/command-line-injection) — cada item
            // vai como argumento literal pro processo filho, sem concatenar/escapar string manualmente.
            processStartInfo.ArgumentList.Add(inputFile);
            processStartInfo.ArgumentList.Add(outputFile);
            processStartInfo.ArgumentList.Add(corr);
            processStartInfo.ArgumentList.Add(_logDirFromApi);

            // ✅ Propagar correlation e log dir para o Decrypt/Lib
            processStartInfo.Environment["LAYOUTPARSER_CORRELATION_ID"] = corr;
            processStartInfo.Environment["LAYOUTPARSER_LOG_DIR"] = _logDirFromApi;

            var execucao = await ExecuteDecryptorProcessAsync(processStartInfo, corr);

            if (execucao.ExitCode != 0)
                throw new DecryptionException($"Legacy decryptor failed (Exit code: {execucao.ExitCode}, corr={corr}): {execucao.Stderr}");

            _logger.LogDebug("Processo legado finalizado: {Output}", execucao.Stdout);
        }

        /// <summary>
        /// Ciclo de vida do processo externo (start, leitura de stdout/stderr, timeout, kill).
        ///
        /// <para><c>protected virtual</c> para que os testes possam capturar o <see cref="ProcessStartInfo"/>
        /// real construído (inclusive <see cref="ProcessStartInfo.ArgumentList"/>) sem depender do
        /// <c>.exe</c> legado — mesmo padrão de
        /// <c>LowCodeTransformationService.ExecuteRunnerProcessAsync</c>.</para>
        /// </summary>
        protected virtual async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteDecryptorProcessAsync(
            ProcessStartInfo processStartInfo,
            string correlationId)
        {
            using var process = new Process();
            process.StartInfo = processStartInfo;

            process.Start();

            // ✅ P1.2 — deadlock corrigido, mesmo padrão do LowCodeTransformationService.ExecuteRunnerProcessAsync:
            // o ReadToEnd() SÍNCRONO bloqueava a thread ANTES do WaitForExit(30000), tornando o timeout
            // inalcançável e prendendo a conexão SQL do loop do reader (esgota o pool). Agora lê stdout/stderr
            // com ReadToEndAsync e corre Task.WhenAll(leituras + exit) contra um Task.Delay(timeout); se o
            // delay vencer, matamos o processo nós mesmos — o timeout passa a ser de fato alcançável.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();

            var allTask = Task.WhenAll(stdoutTask, stderrTask, exitTask);
            using var timeoutCts = new CancellationTokenSource();
            var timeoutTask = Task.Delay(TimeoutMs, timeoutCts.Token);

            var winner = await Task.WhenAny(allTask, timeoutTask);

            if (winner != allTask)
            {
                // ✅ CodeQL cs/log-forging: correlationId pode ecoar o header X-Correlation-ID do
                // cliente — já saneado uma vez no middleware (Program.cs), saneia de novo aqui por
                // segurança (defesa em profundidade, este método também é chamável fora do pipeline HTTP).
                var safeCorrelationId = Services.Logging.LogMessageSanitizer.Sanitize(correlationId);
                _logger.LogError(
                    "Legacy decryptor excedeu o timeout de {TimeoutMs}ms (corr={CorrelationId}) — matando processo",
                    TimeoutMs, safeCorrelationId);

                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception killEx) { _logger.LogWarning(killEx, "Falha ao matar processo do decryptor (corr={CorrelationId})", safeCorrelationId); }

                // Best effort: janela curta para o kill liberar os streams antes de desistir.
                try { await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(2))); } catch { }

                throw new DecryptionException($"Legacy decryptor timeout ({TimeoutMs / 1000} segundos, corr={correlationId})");
            }

            // Encerra o Task.Delay pendente (senão o timer fica vivo até o fim do timeout).
            timeoutCts.Cancel();

            // allTask concluída — propaga eventual exceção real de leitura/exit, se houver.
            await allTask;

            return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }

    }
}
