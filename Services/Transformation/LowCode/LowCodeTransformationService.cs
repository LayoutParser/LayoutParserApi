using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Adapter que executa a transformação pelo aplicativo low-code via runner x86 (processo externo).
    /// Motivo: as DLLs do SysMiddle possuem dependências/arquitetura (x86) incompatíveis com o processo do ASP.NET.
    /// </summary>
    public class LowCodeTransformationService
    {
        private readonly ILogger<LowCodeTransformationService> _logger;
        private readonly LowCodeRunnerOptions _opt;
        private readonly IConfiguration _configuration;

        // ✅ Registrado como Singleton no Program.cs → este semáforo vale para o PROCESSO INTEIRO da
        // API, não só para as N invocações paralelas de um único documento (Task.WhenAll do
        // multi-candidato). Se dois uploads diferentes chegarem ao mesmo tempo, ambos multi-candidato,
        // o limite de concorrência do runner ainda é respeitado no total. Ver LowCode:MaxConcurrentRunners.
        private readonly SemaphoreSlim _runnerSemaphore;

        public LowCodeTransformationService(
            ILogger<LowCodeTransformationService> logger,
            IOptions<LowCodeRunnerOptions> options,
            IConfiguration configuration)
        {
            _logger = logger;
            _opt = options.Value;
            _configuration = configuration;
            _runnerSemaphore = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrentRunners));
        }

        public async Task<string> TransformAsync(
            string inputContent,
            string? mapperId = null,
            string? mapperName = null,
            string? fileName = null,
            string? package = null,
            string? globalFolder = null,
            string? sysmiddleDir = null)
        {
            // ✅ Usar CorrelationId da request (se existir) para rastreabilidade end-to-end
            var correlationId = LayoutParserApi.Services.Logging.CorrelationContext.CurrentId ?? Guid.NewGuid().ToString("N");
            package ??= _opt.Package;
            globalFolder ??= _opt.GlobalFolder;
            sysmiddleDir ??= _opt.SysmiddleDir;
            mapperName ??= _opt.DefaultMapperName;

            if (string.IsNullOrWhiteSpace(_opt.RunnerPath))
                throw new InvalidOperationException("LowCode:RunnerPath não configurado");
            if (string.IsNullOrWhiteSpace(sysmiddleDir))
                throw new InvalidOperationException("LowCode:SysmiddleDir não configurado");
            if (string.IsNullOrWhiteSpace(globalFolder))
                throw new InvalidOperationException("LowCode:GlobalFolder não configurado");
            if (string.IsNullOrWhiteSpace(mapperId) && string.IsNullOrWhiteSpace(mapperName))
                throw new InvalidOperationException("Informe mapperId ou mapperName (ou configure LowCode:DefaultMapperName)");

            var tempDir = Path.Combine(Path.GetTempPath(), "layoutparser-lowcode");
            Directory.CreateDirectory(tempDir);

            var inputPath = Path.Combine(tempDir, $"in_{Guid.NewGuid():N}.txt");
            var outputPath = Path.Combine(tempDir, $"out_{Guid.NewGuid():N}.xml");
            await File.WriteAllTextAsync(inputPath, inputContent ?? "", Encoding.UTF8);

            // ✅ Todos os logs na mesma pasta do API (Logging:File:Directory)
            var logsBase = _configuration["Logging:File:Directory"] ?? Path.Combine(tempDir, "runner-logs");
            Directory.CreateDirectory(logsBase);
            var runnerLogFile = Path.Combine(logsBase, "layoutparserlowcoderunner.log");

            var args = new List<string>
            {
                "--sysmiddleDir", Quote(sysmiddleDir),
                "--globalFolder", Quote(globalFolder),
                "--package", Quote(package ?? ""),
                "--inputFile", Quote(inputPath),
                "--outputFile", Quote(outputPath),
                "--fileName", Quote(fileName ?? Path.GetFileName(inputPath)),
                "--correlationId", Quote(correlationId),
                "--runnerLogFile", Quote(runnerLogFile)
            };

            if (!string.IsNullOrWhiteSpace(mapperId))
            {
                args.Add("--mapperId");
                args.Add(Quote(mapperId));
            }
            else
            {
                args.Add("--mapperName");
                args.Add(Quote(mapperName!));
            }

            var psi = new ProcessStartInfo
            {
                FileName = _opt.RunnerPath,
                Arguments = string.Join(" ", args),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogInformation("Executando transformação low-code: corr={CorrelationId} mapperId={MapperId}, mapperName={MapperName}, runnerLog={RunnerLogFile}",
                correlationId, mapperId, mapperName, runnerLogFile);

            var timeoutSeconds = _opt.RunnerTimeoutSeconds > 0 ? _opt.RunnerTimeoutSeconds : 15;

            string stdout;
            string stderr;
            int exitCode;

            // ✅ Limite de concorrência do runner (processo inteiro da API — ver comentário no campo).
            await _runnerSemaphore.WaitAsync();
            try
            {
                using var p = Process.Start(psi);
                if (p == null)
                    throw new Exception("Falha ao iniciar processo do runner low-code");

                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                var exitTask = p.WaitForExitAsync();

                // ✅ Timeout cobre o ciclo de vida INTEIRO do processo (leitura de stdout/stderr + exit),
                // não só a chamada a WaitForExitAsync isoladamente: se só a espera de exit tivesse
                // CancellationToken, uma leitura de stream travada (processo não fecha os handles)
                // ainda escaparia do timeout — Task.WhenAll(stdout, stderr, exit) só resolve quando o
                // processo de fato morre/fecha os pipes, então corremos essa combinação contra um
                // Task.Delay simples e, se o delay vencer, matamos o processo nós mesmos.
                var allTask = Task.WhenAll(stdoutTask, stderrTask, exitTask);
                var winner = await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));

                if (winner != allTask)
                {
                    _logger.LogError(
                        "Runner low-code excedeu o timeout de {TimeoutSeconds}s (corr={CorrelationId}, mapperId={MapperId}, mapperName={MapperName}) — matando processo",
                        timeoutSeconds, correlationId, mapperId, mapperName);
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception killEx) { _logger.LogWarning(killEx, "Falha ao matar processo do runner low-code após timeout (corr={CorrelationId})", correlationId); }

                    // Best effort: dá uma janela curta pro kill liberar os streams antes de desistir,
                    // só para não deixar as Tasks de leitura penduradas sem observação.
                    try { await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(2))); } catch { }

                    throw new TimeoutException($"Runner low-code excedeu o timeout de {timeoutSeconds}s (corr={correlationId}, mapperId={mapperId}, mapperName={mapperName})");
                }

                // allTask já concluída — propaga eventual exceção real de leitura/exit, se houver.
                await allTask;
                stdout = stdoutTask.Result;
                stderr = stderrTask.Result;
                exitCode = p.ExitCode;
            }
            finally
            {
                _runnerSemaphore.Release();
            }

            if (exitCode != 0)
            {
                string runnerLog = "";
                try { if (File.Exists(runnerLogFile)) runnerLog = await File.ReadAllTextAsync(runnerLogFile, Encoding.UTF8); } catch { }
                _logger.LogError("Runner low-code falhou (corr={CorrelationId}, exit={ExitCode}). stderr={Stderr}\nrunnerLog:\n{RunnerLog}",
                    correlationId, exitCode, stderr, runnerLog);
                throw new Exception($"Low-code runner falhou (exit={exitCode}): {stderr}");
            }

            if (!File.Exists(outputPath))
                throw new Exception($"Runner não gerou outputFile: {outputPath}. stdout={stdout}");

            var output = await File.ReadAllTextAsync(outputPath, Encoding.UTF8);

            // Best effort cleanup
            TryDelete(inputPath);
            TryDelete(outputPath);

            return output;
        }

        private static string Quote(string s)
            => $"\"{s?.Replace("\"", "\\\"")}\"";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}


