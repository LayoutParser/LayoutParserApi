<#
.SYNOPSIS
    Gate de equivalencia do runner low-code: publica o .exe numa Bin do Sysmiddle e valida a saida
    byte a byte contra o gabarito.

.DESCRIPTION
    Existe porque a validacao do runner e a unica coisa que NAO se prova por leitura de codigo: o
    LayoutParserLowCodeRunner.exe e net481/x86 e resolve dependencias pelo diretorio do proprio
    .exe, entao "funciona" depende de ONDE ele esta. O discriminador e a versao do log4net na Bin
    (2.x = apta; 1.2.13.0 estoura em InstanceFactory.Initialize()).

    Este script mora no repositorio, e nao numa mensagem para copiar e colar, por um motivo
    pratico: o comando tem continuacoes de linha e colar isso num console PowerShell interativo
    quebra o parser. Script em arquivo nao tem esse problema.

.PARAMETER Bin
    Diretorio da Bin do Sysmiddle onde publicar e executar. Default: a instancia local conhecida.

.PARAMETER GlobalFolder
    Catalogo de mappers. Os dois catalogos conhecidos listam 170 mapeadores e ambos contem o mapper
    do gabarito - o globalFolder nao e a variavel do teste.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\LowCodeRunner\Test-RunnerGate.ps1

.NOTES
    Esperado: EXIT=0, 48-137s, 4246 bytes.
    Diagnostico de falha:
      exit 9              -> --package nao chegou (protocolo API<->runner)
      exit 0, 2852 bytes  -> mapper errado (MAP_MARELLI_ homonimo), nao regressao
      erro em InstanceFactory.Initialize() -> a Bin nao e apta
#>
[CmdletBinding()]
param(
    [string]$Bin          = 'C:\appconnector\App\Bin',
    [string]$GlobalFolder = 'C:\inetpub\wwwroot\layoutparser\globalfolder',
    [string]$RepoRoot     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

$Package  = '938f9978-836f-48c1-9c0f-c2898caf4b20'
$MapperId = 'MAP_f31a6758-69c9-4cf6-92d2-24f0e27a1ab5'   # MAP_MQSERIES_SEND_ENV_TXT_XML_NFE
$Amostra  = 'QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series.txt'

$runnerSrc = Join-Path $PSScriptRoot 'Functions'
$inputFile = Join-Path $RepoRoot ".claude\tmp\exemplos\txt input\$Amostra"
$gabarito  = Join-Path $RepoRoot ".claude\tmp\exemplos\xml output\QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series-11072026094950273-env.xml"

foreach ($p in @($Bin, $GlobalFolder, $inputFile)) {
    if (-not (Test-Path $p)) { Write-Error "Caminho nao encontrado: $p"; exit 1 }
}

# A Bin precisa ter log4net 2.x - checar ANTES de copiar evita um erro obscuro de assembly.
$l4n = Join-Path $Bin 'log4net.dll'
$ver = if (Test-Path $l4n) { (Get-Item $l4n).VersionInfo.FileVersion } else { '(ausente)' }
Write-Host "Bin:      $Bin"
Write-Host "log4net:  $ver$(if ($ver -notlike '2.*') { '   <-- NAO APTA: o runner vai estourar em InstanceFactory.Initialize()' })"
Write-Host ""

Copy-Item (Join-Path $runnerSrc 'LayoutParserLowCodeRunner.exe') $Bin -Force
$cfg = Join-Path $runnerSrc 'LayoutParserLowCodeRunner.exe.config'
if (Test-Path $cfg) { Copy-Item $cfg $Bin -Force }
Write-Host "Runner publicado em $Bin"

$outputFile = Join-Path $env:TEMP 'gate-runner-out.xml'
Remove-Item $outputFile -ErrorAction SilentlyContinue

Write-Host "Executando (esperado 48-137s)..."
$sw = [Diagnostics.Stopwatch]::StartNew()
& (Join-Path $Bin 'LayoutParserLowCodeRunner.exe') @(
    '--globalFolder', $GlobalFolder
    '--package',      $Package
    '--mapperId',     $MapperId
    '--inputFile',    $inputFile
    '--outputFile',   $outputFile
    '--fileName',     $Amostra
)
$exit = $LASTEXITCODE
$sw.Stop()

Write-Host ""
Write-Host "EXIT=$exit   tempo=$([math]::Round($sw.Elapsed.TotalSeconds,1))s"

if (-not (Test-Path $outputFile)) {
    Write-Host "SAIDA: nao gerada." -ForegroundColor Red
    if ($exit -eq 9) { Write-Host "  exit 9 = --package vazio: LowCode:Package nao chegou ao runner." -ForegroundColor Yellow }
    exit 1
}

$bytes = (Get-Item $outputFile).Length
Write-Host "SAIDA: $bytes bytes   (esperado 4246)"

if ($bytes -eq 2852) {
    Write-Host "  2852 bytes = MAPPER ERRADO (MAP_MARELLI_ homonimo), nao regressao do runner." -ForegroundColor Yellow
}

# Comparacao contra o gabarito: tolera APENAS o espaco duplo em '<?xml  version=', que vem do
# produtor do gabarito (pipeline do connector) e nao do runner.
if (Test-Path $gabarito) {
    $a = (Get-Content $outputFile -Raw) -replace '<\?xml\s+version=', '<?xml version='
    $b = (Get-Content $gabarito   -Raw) -replace '<\?xml\s+version=', '<?xml version='
    if ($a -ceq $b) {
        Write-Host "GATE: EQUIVALENTE ao gabarito (normalizado o espaco duplo do produtor)." -ForegroundColor Green
        exit 0
    }
    Write-Host "GATE: DIVERGE do gabarito." -ForegroundColor Red
    Write-Host "  saida=$bytes bytes  gabarito=$((Get-Item $gabarito).Length) bytes"
    exit 1
}

Write-Host "Gabarito nao encontrado em $gabarito - validado so o tamanho."
exit $(if ($bytes -eq 4246) { 0 } else { 1 })
