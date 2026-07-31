# Handoff — Ponte de log AiMetrics (VM Linux → API Windows)

> Decisão de `@lp-architect` (Aria), 2026-07-30. Opção **B** escolhida pelo dono do projeto.
> Executores: `@lp-backend-dev` (Dex, item 1) e `@lp-devops` (Gage, itens 2 e 3).
> Contexto: [plano-metricas-ia-servidor-producao.md](plano-metricas-ia-servidor-producao.md) §6,
> [handoff-frontend-gap-3-painel-ia-metrics.md](handoff-frontend-gap-3-painel-ia-metrics.md),
> [handoff-job2-cypress-batch.md](handoff-job2-cypress-batch.md).

## Problema

O painel do Gap 3 lê um arquivo que o job nunca escreve:

| Quem | Escreve/lê onde | Máquina |
|------|-----------------|---------|
| `metrics-batch` (Job 1) | `~/layoutparser-ai-metrics/Logs/layoutparserapi.log` | VM Ubuntu `172.25.32.31` |
| `UnifiedLogReaderService` | `Logging:File:Directory` + nomes fixos ([UnifiedLogReaderService.cs:81-87](../../Services/Logging/UnifiedLogReaderService.cs:81)) | Windows Server (API) |

Consequência dupla: `GET /api/ai-metrics/generations` responde `totalCount: 0` mesmo com o job
tendo rodado, **e** o merge do Endpoint 3 nunca casa — a linha `"Cypress validado."` é gravada no
log da API (Windows) enquanto as gerações estão no log da VM (Linux). Arquivos distintos, nenhum
leitor vê os dois.

## Decisão: 4ª fonte de leitura + cópia periódica (pull)

Não acoplar o job à disponibilidade da API (descartadas: POST por geração, sink de rede).
O arquivo precisa estar no diretório de logs da API, com **nome próprio** — e as linhas precisam
passar pelo `ApiLinePattern`, o que **hoje não acontece** (ver Item 0).

### Item 0 — `@lp-backend-dev` (Dex): o regex rejeita TODA linha do job ⚠️

> Correção de uma premissa errada da primeira versão deste documento (Aria, 2026-07-30). Eu havia
> afirmado que o formato "já bate". **Não bate** — achado do `qa-gate` do Quinn, confirmado
> independentemente contra o log real.

| Origem | `outputTemplate` | Tem `[Corr:]`? |
|--------|------------------|----------------|
| API ([Program.cs:133](../../Program.cs:133)) | `[ts] [LVL] [Corr:{CorrelationId}] [Src:{Source}] msg` | sim |
| `metrics-batch` ([MetricsBatchRunner.cs:51](../../ai/XslSynth/Metrics/MetricsBatchRunner.cs:51)) | `[ts] [LVL] [Src:{Source}] msg` | **não** |

O `ApiLinePattern` ([UnifiedLogReaderService.cs:31](../../Services/Logging/UnifiedLogReaderService.cs:31))
torna opcional só o grupo `[Src:...]`; `[Corr:...]` é obrigatório. Linha real hoje em disco
(`Logs/layoutparserapi.log:12810`):

```
[2026-07-29 14:57:48.461] [INF] [Src:AiMetrics] Geracao concluida. Layout=CTe\2.00a\... Sucesso=True
```

→ não casa → descartada. **Os 3 endpoints do Gap 3 retornam vazio hoje, mesmo rodando na mesma
máquina.** A ponte de arquivo (itens 1-2) é necessária mas não suficiente.

**Correção:** tornar `[Corr:...]` opcional, no mesmo estilo do `[Src:...]`:

```csharp
@"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]\s\[(?<level>[A-Za-z]+)\]"
+ @"(?:\s\[Corr:(?<corr>[^\]]*)\])?(?:\s\[Src:(?<source>[^\]]*)\])?\s(?<message>.*)$"
```

Preferir isso a alinhar o template do `MetricsBatchRunner`: o regex recupera também o histórico já
gravado, sem re-rodar lote nenhum, e sem redeploy do CLI na VM.

### Item 1 — `@lp-backend-dev` (Dex): registrar a 4ª fonte

Em [UnifiedLogReaderService.cs](../../Services/Logging/UnifiedLogReaderService.cs), no
`GetLogsAsync`, ao lado das 3 fontes existentes:

```csharp
var aiTask = ReadSourceAsync(logDirectory, "layoutparserai.log", fixedSource: null, ApiLinePattern);
```

e incluir `aiTask` no `Task.WhenAll` e no `Concat`.

Pontos **não negociáveis** do desenho:

1. **`fixedSource: null`**, igual à fonte da API — não `fixedSource: "AiMetrics"`. O `Source` tem
   que vir do grupo `[Src:...]` da própria linha; forçar constante quebraria o filtro
   `Source=AiMetrics` do `AiMetricsReaderService` para linhas de outros Sources que caiam no mesmo
   arquivo, e mascararia o resumo de lote.
2. **Nome do arquivo hardcoded**, como os outros 3 — **não** adicionar chave nova no `appsettings.json`.
   Motivo operacional concreto: o `ci-dev.yml` **preserva** o `appsettings.json` do destino e só faz
   backup ([ci-dev.yml:233-243](../../.github/workflows/ci-dev.yml:233)). Uma chave nova no JSON do
   repo **não chegaria** ao servidor — exigiria edição manual no destino a cada ambiente.
3. `ApiLinePattern` já cobre o formato; **não criar regex nova**.
4. `MaxFilesPerSource` e a paginação existentes se aplicam sem mudança.

Nada mais muda: `AiMetricsReaderService` filtra por `Source=AiMetrics` **através de todas as fontes**,
então o merge geração×Cypress passa a casar automaticamente, mesmo com as duas linhas vindo de
arquivos diferentes.

### Item 2 — `@lp-devops` (Gage): a cópia — **pull do Windows, não push da VM**

Correção de detalhe em relação à proposta original ("script na VM faz `scp`"): a direção certa é
**o Windows puxar**. A chave `layoutparser_automation` e o caminho Windows→VM já existem e já foram
usados no deploy (§6 do plano); o inverso exigiria provisionar servidor SSH no Windows Server —
trabalho novo, superfície nova, sem ganho.

Tarefa agendada no `WINSRV2022-LIB` (de hora em hora, arquivo de poucos KB):

```powershell
# Cópia atômica: baixa em .tmp e só então substitui — evita a API ler arquivo pela metade.
scp -i "$env:USERPROFILE\.ssh\layoutparser_automation" `
    elson@172.25.32.31:~/layoutparser-ai-metrics/logs/layoutparserapi.log `
    "C:\inetpub\wwwroot\layoutparser\api\logs\layoutparserai.log.tmp"
Move-Item -Force "C:\inetpub\wwwroot\layoutparser\api\logs\layoutparserai.log.tmp" `
                 "C:\inetpub\wwwroot\layoutparser\api\logs\layoutparserai.log"
```

> ⚠️ **`logs/` minúsculo — não `Logs/`.** Achado do `@lp-devops` ao inspecionar a VM: o
> `run-metrics-batch.sh` **instalado no cron** passa `--log-dir "$APP_DIR/logs"`, e esse diretório
> está vazio hoje. O único log existente está em `Logs/` (maiúsculo) porque veio do teste `--limit 2`,
> que rodou sem `--log-dir` e caiu no default (`AppContext.BaseDirectory\Logs`,
> [ai/XslSynth/Program.cs:804-807](../../ai/XslSynth/Program.cs:804)). Em Linux os dois são
> diretórios distintos: apontar o `scp` para `Logs/` copiaria para sempre o arquivo velho de 3
> linhas, e o painel exibiria dado de teste como se fosse a rodada de sábado. Confirmar o destino
> real logo após o início da rodada, antes de confiar no número.

**Dívida associada:** o `run-metrics-batch.sh` **não é versionado** — só existe na VM, e diverge do
wrapper versionado [Scripts/vm/run-metrics-then-cypress.sh](../../Scripts/vm/run-metrics-then-cypress.sh)
(que usa `Logs/`). Normalizar o case e versionar o script é follow-up pós-sábado; mexer no cron ativo
véspera de rodada é risco desnecessário.

> ⚠️ **O risco desta tarefa é o nome de destino.** O arquivo na VM se chama `layoutparserapi.log`
> — idêntico ao log ativo da API. Copiar sem renomear **destrói o log da API em produção**. O
> destino é `layoutparserai.log`, sempre.

Periodicidade de hora em hora (em vez de um disparo único pós-batch) porque o lote leva ~3-4h e a
hora exata de término varia — o painel fica fresco sem depender de acertar o horário.

### Item 3 — `@lp-devops` (Gage): deploy da branch

Bloqueio separado, mas no mesmo caminho crítico: os endpoints do Gap 3 **não estão no servidor**
(ver seção "Validação" abaixo). `origin/develop` está em `ad775ee`; os commits que criam o
`AiMetricsController` estão só no clone local. O `ci-dev.yml` dispara em push para `develop`.

## Validação (nesta ordem)

1. Após o deploy: `GET /api/ai-metrics/summary` deve responder **200** (hoje: 404).
   Ainda com `totalGeracoes: 0` — esperado, a ponte ainda não rodou.
2. Após a primeira cópia: `totalGeracoes > 0` e `ultimaRodada` preenchida.
3. `GET /api/logs?source=AiMetrics` deve listar as mesmas linhas (prova que a 4ª fonte entrou no
   leitor unificado, não só no parser de métricas).
4. Merge: `POST /api/ai-metrics/cypress-result` com um `layout` que exista na lista → o mesmo item
   em `/generations` passa a vir com `cypressValidado` preenchido em vez de `null`.

## O que esta ponte NÃO resolve

Os gaps estruturais do Job 2 seguem abertos e independem daqui — candidato não persistido, artefato
XSLT vs. XML esperado pelo Pollux, e os 4/54 pares elegíveis. Ver
[handoff-job2-cypress-batch.md](handoff-job2-cypress-batch.md).
