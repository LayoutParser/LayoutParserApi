// Double do runner LowCode real para teste e2e de TryEnqueueAiCandidate (issue #104).
// Imita o CONTRATO DE PROCESSO observado pela API (LowCodeTransformationService), na forma
// NOMEADA que é a única que a API efetivamente fala (ver tools/LowCodeRunner/RunnerArgs.cs,
// RunnerArgsParser.ParseNomeado). Não reimplementa a forma posicional (uso offline) nem
// qualquer lógica de mapeamento Sysmiddle — só os pontos observáveis de fora: argumentos aceitos,
// arquivo de saída, exit code, e (quando configurado) o arquivo de log.
using System;
using System.IO;

namespace FakeLowCodeRunner;

internal static class Program
{
    // Espelha tools/LowCodeRunner/RunnerArgs.cs (RunnerExitCodes) — mesmos códigos, mesmo
    // significado, para que os testes e2e do lado API exerçam os caminhos reais de tratamento
    // de exit code em LowCodeTransformationService sem precisar do runner de verdade.
    private const int ExitOk = 0;
    private const int ExitFatal = 1;
    private const int ExitUsageError = 2;
    private const int ExitInvalidNamedArgument = 7;
    private const int ExitPackageNotConfigured = 9;

    private static int Main(string[] args)
    {
        string? inputFile = null;
        string? outputFile = null;
        string? package = null;
        string? runnerLogFile = null;
        string? correlationId = null;

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                continue;

            string nome = token[2..];
            string? valor = (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                ? args[++i]
                : null;

            switch (nome)
            {
                case "inputFile": inputFile = valor; break;
                case "outputFile": outputFile = valor; break;
                case "package": package = valor; break;
                case "runnerLogFile": runnerLogFile = valor; break;
                case "correlationId": correlationId = valor; break;
                // demais flags (--globalFolder, --mapperId, --mapperName, --fileName,
                // --sysmiddleDir, --nfePostProcessing) são aceitas e ignoradas: o double não
                // precisa delas para decidir o cenário, só precisa não falhar o parse por
                // "argumento desconhecido" quando a API as envia normalmente.
            }
        }

        // Cenário determinístico via env var — não via argumento de linha de comando, porque o
        // chamador real (LowCodeTransformationService) não tem como/motivo para mandar um
        // argumento assim; o teste e2e controla o cenário de fora do processo, exatamente como
        // controlaria "o runner real quebrou hoje" via ambiente do host de teste.
        string cenario = Environment.GetEnvironmentVariable("FAKE_RUNNER_SCENARIO") ?? "success";

        Log(runnerLogFile, correlationId, $"FakeLowCodeRunner iniciado. Cenario={cenario}");

        if (string.IsNullOrWhiteSpace(package))
        {
            Log(runnerLogFile, correlationId, "package ausente/vazio.");
            return ExitPackageNotConfigured;
        }

        if (string.IsNullOrWhiteSpace(inputFile) || string.IsNullOrWhiteSpace(outputFile))
        {
            Log(runnerLogFile, correlationId, "inputFile/outputFile ausente.");
            return ExitInvalidNamedArgument;
        }

        switch (cenario)
        {
            case "success":
                return CenarioSucesso(inputFile, outputFile, runnerLogFile, correlationId);

            case "timeout":
                // Simula um runner travado: dorme além de qualquer timeout razoável configurado
                // pela API. O teste e2e é quem decide o timeout e observa o cancelamento/kill.
                Log(runnerLogFile, correlationId, "Simulando timeout — dormindo indefinidamente.");
                System.Threading.Thread.Sleep(Timeout.Infinite);
                return ExitFatal; // inalcançável; mantido por clareza de contrato.

            case "malformed_output":
                Log(runnerLogFile, correlationId, "Simulando saída malformada.");
                File.WriteAllText(outputFile, "<isto-nao-fecha");
                return ExitOk;

            case "nonzero_exit":
                Log(runnerLogFile, correlationId, "Simulando falha fatal do runner.");
                return ExitFatal;

            case "empty_output":
                Log(runnerLogFile, correlationId, "Simulando saída vazia (arquivo não escrito).");
                return ExitOk;

            case "usage_error":
                return ExitUsageError;

            default:
                Log(runnerLogFile, correlationId, $"Cenario desconhecido: {cenario}");
                return ExitFatal;
        }
    }

    private static int CenarioSucesso(string inputFile, string outputFile, string? runnerLogFile, string? correlationId)
    {
        if (!File.Exists(inputFile))
        {
            Log(runnerLogFile, correlationId, $"Input nao encontrado: {inputFile}");
            return 4; // RunnerExitCodes.InputNotFound
        }

        // Saída determinística e mínima, válida como XML — suficiente para o teste e2e verificar
        // que o candidato chegou a AiCandidateStore com status de sucesso. Não tenta imitar XML
        // fiscal real: isso é responsabilidade dos testes de transformação (Services/Testing),
        // não deste double de processo.
        string xml = $"<FakeLowCodeResult correlationId=\"{correlationId}\" source=\"{Path.GetFileName(inputFile)}\" />";
        File.WriteAllText(outputFile, xml);

        Log(runnerLogFile, correlationId, "Sucesso.");
        return ExitOk;
    }

    private static void Log(string? runnerLogFile, string? correlationId, string mensagem)
    {
        if (string.IsNullOrWhiteSpace(runnerLogFile))
            return;

        try
        {
            File.AppendAllText(runnerLogFile, $"[{DateTime.UtcNow:O}] [{correlationId}] {mensagem}{Environment.NewLine}");
        }
        catch
        {
            // Log é diagnóstico, não pode derrubar o double — mesmo princípio de resiliência
            // do projeto (dotnet-standards.md), aplicado aqui mesmo fora do runtime ASP.NET.
        }
    }
}
