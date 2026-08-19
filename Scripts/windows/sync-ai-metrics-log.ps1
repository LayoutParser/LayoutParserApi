<#
.SYNOPSIS
    Ponte de log AiMetrics (VM Linux -> API Windows).

.DESCRIPTION
    Copia (pull) o log mais recente do metrics-batch (Job 1), gerado na VM Ubuntu
    (172.25.32.31), para o diretorio de logs da API no WINSRV2022-LIB, renomeando
    para 'layoutparserai.log' (nome fixo esperado pela 4a fonte do
    UnifiedLogReaderService).

    Dono: @lp-devops (Gage). Contrato: docs/architecture/handoff-ponte-log-aimetrics.md (Item 2).

    Ponto de entrada da tarefa agendada (Task Scheduler) no WINSRV2022-LIB,
    de hora em hora. Substitui o comando scp de caminho fixo que rodava direto
    na definicao da tarefa (nao versionado ate esta mudanca).

.NOTES
    Por que glob e nao caminho fixo:
    O MetricsBatchRunner.cs hoje usa RollingInterval.Infinite (nome fixo
    layoutparserapi.log). Ao ser corrigido para RollingInterval.Day (proxima
    etapa, dono @lp-parser-llm), o nome do arquivo de origem passa a variar por
    data (ex.: layoutparserapi20260819.log). Um scp de caminho fixo simplesmente
    para de achar o arquivo nesse dia. Este script tolera os dois formatos:
    baixa todo candidato que bater no glob 'layoutparserapi*.log' e escolhe o
    mais recente por LastWriteTime entre os baixados.

    Copia atomica: baixa em .tmp e so entao substitui via Move-Item -Force —
    evita a API ler o arquivo de destino pela metade.

    ATENCAO ao case do diretorio de origem: 'logs/' minusculo (nao 'Logs/') —
    ver nota do handoff sobre run-metrics-batch.sh usar --log-dir "$APP_DIR/logs".

    ATENCAO ao nome do arquivo de origem: o log ATIVO da API Windows tambem se
    chama layoutparserapi*.log. O destino aqui e SEMPRE 'layoutparserai.log'
    (sem o 'p' de 'api' duplicado) — nunca sobrescrever o log da API.

.PARAMETER SshKeyPath
    Caminho da chave privada usada para o scp (default: layoutparser_automation
    no perfil do usuario que roda a tarefa agendada).

.PARAMETER RemoteHost
    Usuario@host da VM de origem (default: elson@172.25.32.31).

.PARAMETER RemoteLogDir
    Diretorio remoto onde o Job 1 escreve os logs (default:
    ~/layoutparser-ai-metrics/logs — minusculo, ver nota acima).

.PARAMETER DestDir
    Diretorio local (WINSRV2022-LIB) onde a API le os logs via
    UnifiedLogReaderService (default: C:\inetpub\wwwroot\layoutparser\api\logs).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Scripts\windows\sync-ai-metrics-log.ps1
#>

[CmdletBinding()]
param(
    [string]$SshKeyPath = "$env:USERPROFILE\.ssh\layoutparser_automation",
    [string]$RemoteHost = "elson@172.25.32.31",
    [string]$RemoteLogDir = "~/layoutparser-ai-metrics/logs",
    [string]$DestDir = "C:\inetpub\wwwroot\layoutparser\api\logs"
)

$ErrorActionPreference = "Stop"

function Write-Log {
    param([string]$Message)
    $ts = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    Write-Output "$ts [sync-ai-metrics-log] $Message"
}

$finalDest = Join-Path $DestDir "layoutparserai.log"
$tmpDest   = "$finalDest.tmp"

# Diretorio temporario de staging para o download por glob — isolado por run
# para nao colidir com outra execucao concorrente da mesma tarefa agendada.
$stagingDir = Join-Path $env:TEMP "ai-metrics-sync-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

try {
    Write-Log "Baixando candidatos via glob: ${RemoteHost}:${RemoteLogDir}/layoutparserapi*.log -> $stagingDir"

    # scp com glob remoto: expande no shell da VM (bash), nao no Windows.
    # -o BatchMode=yes evita a tarefa agendada travar esperando prompt interativo
    # se a chave falhar/expirar.
    $scpArgs = @(
        "-i", $SshKeyPath,
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=accept-new",
        "${RemoteHost}:${RemoteLogDir}/layoutparserapi*.log",
        "$stagingDir\"
    )
    & scp @scpArgs
    if ($LASTEXITCODE -ne 0) {
        throw "scp falhou (exit=$LASTEXITCODE) ao buscar layoutparserapi*.log em ${RemoteHost}:${RemoteLogDir}"
    }

    $candidatos = Get-ChildItem -Path $stagingDir -Filter "layoutparserapi*.log" -File -ErrorAction SilentlyContinue
    if (-not $candidatos -or $candidatos.Count -eq 0) {
        throw "Nenhum arquivo layoutparserapi*.log baixado de ${RemoteHost}:${RemoteLogDir} — verifique o caminho remoto (case-sensitive: 'logs/' minusculo)."
    }

    $maisRecente = $candidatos | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Log "Selecionado por LastWriteTime mais recente: $($maisRecente.Name) ($($maisRecente.LastWriteTime))"

    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null

    Copy-Item -Path $maisRecente.FullName -Destination $tmpDest -Force
    Move-Item -Path $tmpDest -Destination $finalDest -Force

    Write-Log "OK — $finalDest atualizado a partir de $($maisRecente.Name)."
}
finally {
    Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
