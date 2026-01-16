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

        public LowCodeTransformationService(
            ILogger<LowCodeTransformationService> logger,
            IOptions<LowCodeRunnerOptions> options)
        {
            _logger = logger;
            _opt = options.Value;
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

            // ✅ Todos os logs na mesma pasta do API, por padrão
            var logsBase = _opt.RunnerLogsPath;
            if (string.IsNullOrWhiteSpace(logsBase))
                logsBase = Path.Combine(tempDir, "runner-logs");
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

            using var p = Process.Start(psi);
            if (p == null)
                throw new Exception("Falha ao iniciar processo do runner low-code");

            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (p.ExitCode != 0)
            {
                string runnerLog = "";
                try { if (File.Exists(runnerLogFile)) runnerLog = await File.ReadAllTextAsync(runnerLogFile, Encoding.UTF8); } catch { }
                _logger.LogError("Runner low-code falhou (corr={CorrelationId}, exit={ExitCode}). stderr={Stderr}\nrunnerLog:\n{RunnerLog}",
                    correlationId, p.ExitCode, stderr, runnerLog);
                throw new Exception($"Low-code runner falhou (exit={p.ExitCode}): {stderr}");
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


