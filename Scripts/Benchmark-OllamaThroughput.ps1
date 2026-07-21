<#
.SYNOPSIS
    Mede throughput real (tokens/segundo) de modelos Ollama - item 3.6 do roadmap de IA
    (docs/architecture/ai-roadmap-dispatch.md, 2026-07-21).

.DESCRIPTION
    Contexto: o servidor de produção alvo (BRNDDAPPBLD01) é um Intel i7-4790 (Haswell, 2014,
    4c/8t, AVX2 sem AVX-512), 32GB RAM, sem GPU confirmada. A recomendação de modelo é mirar
    1-2B parâmetros (não 2-4B) como ponto de partida, mas o item 3.6 exige medir tok/s REAL
    nesse servidor antes de comprometer - não existe benchmark público confiável para essa
    CPU específica rodando modelos GGUF via Ollama.

    Este script NÃO estima tokens manualmente: usa as estatísticas nativas que o próprio
    Ollama devolve em /api/generate (eval_count, eval_duration em nanossegundos,
    prompt_eval_count, prompt_eval_duration, load_duration, total_duration) - são exatas
    (vêm do runtime de inferência), não uma aproximação por contagem de caracteres/palavras.
    Contrato conferido nesta sessão contra uma instância Ollama real (v0.31.2): os nomes de
    campo acima existem e vêm em nanossegundos, como assumido abaixo.

    Sobre AVX2: os builds oficiais do Ollama já detectam a CPU em runtime e escolhem a
    variante de backend adequada (cpu / cpu_avx / cpu_avx2) automaticamente - não deveria ser
    necessário recompilar nada manualmente para uma Haswell (que tem AVX2, mas não AVX-512).
    Ainda assim, ao rodar este script na máquina de produção pela primeira vez, vale conferir
    o log de start-up do `ollama serve` para confirmar qual variante foi carregada.

.PARAMETER OllamaUrl
    Base URL do servidor Ollama a testar. Default: http://localhost:11434.
    Para medir o servidor de produção de verdade, este script precisa ser executado A PARTIR
    do próprio BRNDDAPPBLD01 (ou de uma máquina com rede até a porta 11434 dele, se exposta -
    confirmar com @lp-devops se isso é liberado ou só localhost).

.PARAMETER Models
    Lista de modelos candidatos a testar (precisam já estar "pulled" no servidor Ollama alvo -
    este script não faz `ollama pull`). Default: alguns candidatos 1-2B mencionados no roadmap
    (ajustar conforme o que realmente for baixado para teste).

.PARAMETER Prompt
    Prompt representativo do caso de uso real (explicação de erro XSD / diagnóstico fiscal -
    ver ia-fiscal-diagnosis-vision.md §3). Propositalmente não é um dump de XML inteiro nem
    uma pergunta de uma palavra: precisa gerar uma resposta longa o bastante para o tok/s não
    ficar dominado por ruído de poucos tokens (confirmado na prática: uma resposta de 2 tokens
    gera uma medida de tok/s essencialmente sem sentido).

.PARAMETER Runs
    Quantas vezes repetir cada modelo, para suavizar variância (default 3). A primeira
    chamada de cada modelo inclui o tempo de load (fica registrado em LoadDurationS,
    separado do cálculo de tok/s de geração).

.PARAMETER OutputCsv
    Caminho opcional para exportar os resultados detalhados (todas as runs) em CSV.

.EXAMPLE
    # Rodar localmente com os candidatos default
    .\Benchmark-OllamaThroughput.ps1

.EXAMPLE
    # Rodar contra o servidor de produção, com modelos e repetições customizados
    .\Benchmark-OllamaThroughput.ps1 -OllamaUrl "http://BRNDDAPPBLD01:11434" `
        -Models @("qwen2.5:1.5b","llama3.2:1b") -Runs 5 -OutputCsv ".\ollama-bench-brnddappbld01.csv"

.NOTES
    Preparado por @lp-backend-dev (Dex) - item 3.6 do dispatch de IA.
    NÃO executado contra BRNDDAPPBLD01 nesta sessão: o ambiente de desenvolvimento usado para
    prepará-lo não tem acesso direto a essa máquina de produção. O contrato de resposta do
    Ollama (nomes de campo/unidades) foi validado nesta sessão contra uma instância Ollama
    local (v0.31.2) - mas essa instância roda em hardware completamente diferente do alvo
    (CPU com AVX-512, não Haswell/AVX2-only) e sem nenhum modelo candidato 1-2B pulled, então
    NENHUM número de throughput real foi medido ainda. O que falta para medir de verdade:
    acesso (remoto ou local) ao BRNDDAPPBLD01 com Ollama instalado e os modelos candidatos já
    baixados (`ollama pull`) - ver relatório da tarefa para a lista completa de pendências.
#>

param(
    [string]$OllamaUrl = "http://localhost:11434",

    [string[]]$Models = @("qwen2.5:1.5b", "llama3.2:1b", "gemma2:2b", "smollm2:1.7b"),

    [string]$Prompt = @"
Você é um especialista em validação de XML NFe da SEFAZ.
Um erro foi encontrado durante a validação XSD:
ERRO: Elemento 'infNFe' com filho inválido 'dest'. Elementos esperados: 'emit'.
CAMPO AFETADO: dest
Por favor, forneça:
1. Uma explicação clara e simples do erro
2. O que causou este erro
3. Como corrigir o problema
4. Um exemplo de valor correto (se aplicável)
"@,

    [int]$Runs = 3,

    [string]$OutputCsv = ""
)

function Test-OllamaReachable {
    param([string]$BaseUrl)

    try {
        $resp = Invoke-RestMethod -Uri "$BaseUrl/api/version" -Method Get -TimeoutSec 5
        Write-Host "Ollama respondendo em $BaseUrl (versao $($resp.version))" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "Nao foi possivel conectar em $BaseUrl - $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Invoke-OllamaGenerate {
    param(
        [string]$BaseUrl,
        [string]$Model,
        [string]$Prompt
    )

    $body = @{
        model  = $Model
        prompt = $Prompt
        stream = $false
    } | ConvertTo-Json -Compress

    return Invoke-RestMethod -Uri "$BaseUrl/api/generate" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 300
}

function Measure-OllamaModel {
    param(
        [string]$BaseUrl,
        [string]$Model,
        [string]$Prompt,
        [int]$Runs
    )

    $samples = @()

    for ($i = 1; $i -le $Runs; $i++) {
        Write-Host "  Run $i/$Runs..." -NoNewline

        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $resp = Invoke-OllamaGenerate -BaseUrl $BaseUrl -Model $Model -Prompt $Prompt
            $sw.Stop()

            # Estatisticas nativas do Ollama (nanossegundos -> segundos)
            $evalCount = $resp.eval_count
            $evalDurationS = $resp.eval_duration / 1e9
            $promptCount = $resp.prompt_eval_count
            $promptDurS = $resp.prompt_eval_duration / 1e9
            $loadDurS = $resp.load_duration / 1e9
            $totalDurS = $resp.total_duration / 1e9

            $tokensPerSecond = 0
            if ($evalDurationS -gt 0) {
                $tokensPerSecond = [math]::Round($evalCount / $evalDurationS, 2)
            }

            $samples += [PSCustomObject]@{
                Model           = $Model
                Run             = $i
                LoadDurationS   = [math]::Round($loadDurS, 2)
                PromptTokens    = $promptCount
                PromptEvalS     = [math]::Round($promptDurS, 2)
                GenTokens       = $evalCount
                GenDurationS    = [math]::Round($evalDurationS, 2)
                TokensPerSecond = $tokensPerSecond
                TotalDurationS  = [math]::Round($totalDurS, 2)
                WallClockS      = [math]::Round($sw.Elapsed.TotalSeconds, 2)
            }

            Write-Host (" {0} tok/s ({1} tokens em {2}s de geracao)" -f $tokensPerSecond, $evalCount, [math]::Round($evalDurationS, 2)) -ForegroundColor Cyan
        }
        catch {
            Write-Host " FALHOU: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    return $samples
}

# ---- main ----

if (-not (Test-OllamaReachable -BaseUrl $OllamaUrl)) {
    Write-Host "Abortando: Ollama inacessivel em $OllamaUrl." -ForegroundColor Red
    exit 1
}

$todosResultados = @()

foreach ($model in $Models) {
    Write-Host ""
    Write-Host "=== Modelo: $model ===" -ForegroundColor Yellow
    $resultados = Measure-OllamaModel -BaseUrl $OllamaUrl -Model $model -Prompt $Prompt -Runs $Runs
    $todosResultados += $resultados
}

if ($todosResultados.Count -eq 0) {
    Write-Host ""
    Write-Host "Nenhum resultado coletado (todos os modelos falharam ou nao estao 'pulled' no servidor)." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== Resumo (media por modelo) ===" -ForegroundColor Yellow
$resumo = $todosResultados | Group-Object Model | ForEach-Object {
    [PSCustomObject]@{
        Model              = $_.Name
        Runs               = $_.Count
        AvgTokensPerSecond = [math]::Round(($_.Group | Measure-Object TokensPerSecond -Average).Average, 2)
        AvgLoadDurationS   = [math]::Round(($_.Group | Measure-Object LoadDurationS -Average).Average, 2)
        AvgGenTokens       = [math]::Round(($_.Group | Measure-Object GenTokens -Average).Average, 1)
    }
}
$resumo | Format-Table -AutoSize

if ($OutputCsv) {
    $todosResultados | Export-Csv -Path $OutputCsv -NoTypeInformation -Encoding UTF8
    Write-Host "Resultados detalhados exportados para $OutputCsv" -ForegroundColor Green
}
