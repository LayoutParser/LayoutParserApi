# Job 1 — persistência de candidatos do `metrics-batch` (issue #35)

> **Autora:** `@lp-parser-llm` (Lia) · **Data:** 2026-08-12
> **Escopo:** documenta o estado ATUAL da persistência de artefatos do
> `ai/XslSynth --mode=metrics-batch` (Job 1) para consumo pelo Job 2 (Pollux/Cypress) e
> processos de auditoria/reprocessamento.
>
> A especificação normativa e completa do contrato **Job 1 → Job 2** já existe em
> [`handoff-job2-cypress-batch.md`](handoff-job2-cypress-batch.md) §2/§3 (autoria `@lp-architect`).
> Este documento **não substitui** aquele — é o registro de que os itens A1 (persistência)
> e a retenção de 30 dias, pendentes na issue #35, estão implementados, e onde.

## 1. O que já estava resolvido antes desta entrega

A persistência do candidato (run dir + `candidates/*.xml` + `manifest.json`, commit atômico)
já havia sido implementada na branch `feat/job1-persistencia-candidatos` (commit `9e48650`,
"Ingestão e publicação robusta de métricas de IA") e **já está mergeada em `develop`**. O
código vive em:

- `ai/XslSynth/Metrics/RunManifest.cs` — `RunManifest`, `ManifestCandidate`, `RunArtifactWriter`.
- `ai/XslSynth/Metrics/MetricsBatchRunner.cs` — orquestra o loop por caso e publica o run dir
  ao final (`writer.TryCommit(manifesto)`).
- `ai/XslSynth/Program.cs` (`RunMetricsBatchAsync`) — resolve `--run-dir`/`LP_METRICS_RUN_DIR`
  (ou `$METRICS_HOME/runs/<runId>` quando só `METRICS_HOME` está setado) e repassa para
  `MetricsBatchOptions.RunDirectory`.

Shape do run dir e do `manifest.json`: ver `handoff-job2-cypress-batch.md` §2 (não duplicado
aqui — é o contrato normativo, mudar o shape ali exige bump de `schemaVersion`).

## 2. O que esta entrega adicionou: retenção de 30 dias

**Decisão do dono do projeto:** runs com mais de **30 dias** são removidos automaticamente.

### Onde

`RunArtifactWriter.CleanupOldRuns(string? runsDir, string currentRunId, int retentionDays, Action<string>? log)`
em `ai/XslSynth/Metrics/RunManifest.cs`. É chamado pelo **construtor** de `RunArtifactWriter`,
antes de `Directory.CreateDirectory(CandidatesDirectory)` — ou seja, toda vez que o Job 1
está prestes a publicar um run novo, ele primeiro poda os runs antigos do mesmo `runs/`.

### Por que aqui e não num cron/timer separado

Não introduz mais um processo agendado na VM (`172.25.32.31`) — cada rodada do Job 1 já
garante a poda das anteriores como efeito colateral da própria execução. Consistente com o
resto do projeto: sem infraestrutura nova para uma necessidade que o próprio fluxo existente
já cobre.

### Critério de remoção

- Itera os subdiretórios diretos de `runs/` (o pai de `RunDirectory`).
- Nome da pasta é parseado como runId no formato `yyyyMMdd'T'HHmmss'Z'` (mesmo formato de
  `RunArtifactWriter.NewRunId`). Pasta cujo nome **não** segue essa convenção é **ignorada**
  — nunca mexe em algo que não é um artefato nosso.
- Runs mais antigos que `DateTime.UtcNow - 30 dias` são removidos com `Directory.Delete(dir,
  recursive: true)`.
- O run que está sendo criado agora (`currentRunId`) nunca é removido, mesmo que por algum
  motivo o clock da VM esteja desalinhado.
- O ponteiro `runs/latest` é um **arquivo**, não um diretório — `EnumerateDirectories` já o
  ignora, nenhuma lógica extra necessária.

### Resiliência

Best-effort e por-diretório, no mesmo espírito do resto do writer (`TryWriteCandidateXml`,
`TryCommit`): uma falha ao remover UM run antigo (ex.: arquivo travado por outro processo,
permissão) é logada como aviso e a limpeza segue para o próximo diretório — **nunca** impede
a criação/publicação do run atual. Não há como uma falha de limpeza derrubar o Job 1.

### Constante

`RunArtifactWriter.DefaultRetentionDays = 30` — hoje não é parametrizável por CLI/env var
(YAGNI: a decisão do dono do projeto foi um valor fixo). Se o valor precisar variar por
ambiente no futuro, é um `--retention-days`/`LP_METRICS_RETENTION_DAYS` a acrescentar em
`RunMetricsBatchAsync` (`Program.cs`), repassado a `MetricsBatchOptions` e daí ao construtor
de `RunArtifactWriter` — não uma mudança de desenho.

## 3. Contrato para o Job 2 (Pollux/Cypress) — o que muda, o que não muda

**Não muda nada do contrato de leitura.** O Job 2 continua lendo `manifest.json` do run mais
recente (via `runs/latest` ou um `runId` explícito recebido do wrapper) exatamente como descrito
em `handoff-job2-cypress-batch.md` §2/§4. A retenção só afeta runs **antigos** — um run que o
Job 2 está processando neste exato momento tem menos de 30 dias por construção (acabou de ser
publicado pelo Job 1 na mesma execução do wrapper).

**O que o Job 2 (ou qualquer reprocessamento manual) precisa saber:**

- Reprocessar/auditar um run espera contar com uma janela de **até 30 dias** a partir da
  publicação — depois disso, o diretório (candidatos + manifesto) some. Se o Job 2/Pollux
  precisar de retenção mais longa para auditoria de longo prazo, isso é uma mudança de política
  a acordar (não a alterar `DefaultRetentionDays` unilateralmente sem avisar `@lp-parser-llm`
  e `@lp-devops`, já que afeta espaço em disco da VM de produção).
- `cypress-results.ndjson`/`cypress-summary.json` (artefatos do Job 2, escritos dentro do
  próprio `runs/<runId>/`, ver §3.1 do handoff) são removidos junto quando o run expira — a
  limpeza é por diretório inteiro, não seletiva por arquivo.

## 4. Validação manual realizada nesta entrega

Sem servidor Ollama disponível nesta sessão (ambiente de desenvolvimento, não a VM de
produção com GPU/CPU dedicada), a validação foi feita com `--dry-run` (candidato = XSLT
gabarito do próprio dataset, não passa pelo LLM — existe justamente para exercitar o
pipeline de artefatos sem gastar inferência real, ver doc-comment de `MetricsBatchOptions.DryRun`):

1. Criado manualmente um run dir antigo (`runs/20250101T000000Z`, > 30 dias) com
   `manifest.json` de exemplo.
2. Rodado `dotnet run -- --mode=metrics-batch --dry-run --limit 1 --run-dir runs/20260812T220000Z`
   contra o dataset real (`dataset_pairs_filtered_v2.jsonl`).
3. Confirmado no log: `[run-dir] limpeza de retenção: 1 run(s) com mais de 30 dia(s)
   removido(s).` — o diretório antigo foi removido do disco.
4. Confirmado que o run novo foi publicado corretamente: `runs/20260812T220000Z/manifest.json`
   e `runs/20260812T220000Z/candidates/` presentes, `runs/latest` apontando para o runId novo.

`dotnet build` do projeto `ai/XslSynth` sem erros/warnings.
