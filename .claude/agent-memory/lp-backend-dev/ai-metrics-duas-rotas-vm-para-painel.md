---
name: ai-metrics-duas-rotas-vm-para-painel
description: Existem DUAS rotas concorrentes para levar as gerações da VM Linux ao painel Gap 3 (POST ingest vs. cópia de log); ativar as duas duplica cada geração e o dedup não salva.
metadata:
  type: project
---

O bug "painel do Gap 3 sempre vazio" (log das gerações fica na VM Ubuntu 172.25.32.31, API lê o
diretório do Windows) recebeu **duas** soluções, implementadas em paralelo em 2026-07-30:

- **A — push HTTP:** `POST /api/ai-metrics/generations/ingest` grava a linha `Geracao concluida.`
  no próprio log da API (recomendada no `handoff-job2-cypress-batch.md` §A4).
- **B — ponte de arquivo:** cópia periódica do log da VM para o diretório da API com nome próprio
  (`layoutparserai.log`) + 4ª fonte no `UnifiedLogReaderService` (`handoff-ponte-log-aimetrics.md`,
  marcada como "Opção B escolhida pelo dono do projeto").

**Why:** as duas alimentam o MESMO caminho de leitura (`AiMetricsReaderService` filtrando
`Source=AiMetrics`). Com as duas ativas, cada geração entra duas vezes — e o dedup por
`(Layout, Timestamp)` do reader **não** colapsa o par, porque a linha copiada carrega a hora da VM
e a linha ingerida carrega a hora local do servidor (bases de fuso diferentes). Duplicata infla
totais e médias do painel sem erro nenhum.

**How to apply:** antes de mexer em qualquer ponta desse fluxo, confirme qual rota está ativa
(cron/tarefa de cópia na VM vs. POST no fim do `metrics-batch`). Se ambas estiverem, sinalize —
é decisão de arquitetura, não de implementação. Ortogonal às duas: o `[Corr:...]` opcional no
`ApiLinePattern` é obrigatório para ler linhas geradas pelo CLI `ai/XslSynth`, que não emite esse
campo. Ver [[unified-logging-implementation-2026-07-28]].
