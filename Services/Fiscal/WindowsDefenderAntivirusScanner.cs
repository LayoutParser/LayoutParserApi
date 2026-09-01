using System.Diagnostics;

using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Scanner via Windows Defender (<c>MpCmdRun.exe -Scan -ScanType 3 -File</c>), único AV gratuito
    /// já presente no host (Slice 2 — spec §13, design §4 item 5). Caminho do executável fixo do
    /// Defender no Windows — se o processo não existir/não puder ser iniciado (ambiente sem Defender,
    /// ex.: CI Linux ou máquina sem o componente), degrada para indisponível (<c>null</c>), não lança.
    /// </summary>
    public sealed class WindowsDefenderAntivirusScanner : IAntivirusScanner
    {
        private const string DefenderCliPath = @"C:\Program Files\Windows Defender\MpCmdRun.exe";

        private readonly ILogger<WindowsDefenderAntivirusScanner> _logger;

        public WindowsDefenderAntivirusScanner(ILogger<WindowsDefenderAntivirusScanner> logger)
        {
            _logger = logger;
        }

        public async Task<bool?> ScanAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(DefenderCliPath))
            {
                // Ambiente sem Defender (ex.: não-Windows, ou componente desabilitado) — degrada,
                // nunca falha o pipeline de upload por isso.
                _logger.LogWarning("Windows Defender (MpCmdRun.exe) não encontrado neste host — scan permanece indisponível (artefato fica Pending).");
                return null;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = DefenderCliPath,
                        // -Scan -ScanType 3 = scan customizado de arquivo único; -File aponta o alvo.
                        // Nunca logamos o conteúdo do arquivo, só o caminho (metadado, não payload).
                        Arguments = $"-Scan -ScanType 3 -File \"{filePath}\" -DisableRemediation",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };

                process.Start();
                await process.WaitForExitAsync(cancellationToken);

                // MpCmdRun retorna 0 quando nenhuma ameaça é encontrada; não-zero indica detecção ou
                // erro de execução — tratamos ambos como "não limpo" (fail-closed do lado do scan).
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao executar o scan do Windows Defender — artefato permanece Pending.");
                return null;
            }
        }
    }
}
