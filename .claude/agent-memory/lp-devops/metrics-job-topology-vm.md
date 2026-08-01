---
name: metrics-job-topology-vm
description: VM Ubuntu 172.25.32.31 roda Ollama E o cron semanal do metrics-batch (Job 1) — verificado 2026-07-30; a ponte de log VM→Windows é necessária, e o cron atual grava em logs/ minúsculo
metadata:
  type: project
---

A VM Ubuntu `172.25.32.31` (`UBU220405RUN`) **não hospeda só o Ollama**: ela também roda o
**Job 1 (`metrics-batch`)** via `cron` do usuário `elson`. Verificado por evidência direta em
2026-07-30 (`ssh -i ~/.ssh/layoutparser_automation elson@172.25.32.31 "crontab -l"`):

```
0 0 * * 6 /home/elson/layoutparser-ai-metrics/run-metrics-batch.sh # layoutparser-ai-metrics-batch
```

`~/layoutparser-ai-metrics/` existe com o publish do `XslSynth` + dataset; `systemctl is-active
ollama` → `active`. Ou seja: os dois papéis convivem na mesma VM.

**Why:** havia contradição entre a §6 de `docs/architecture/plano-metricas-ia-servidor-producao.md`
(job na VM) e a memória de topologia do projeto ("VM só hospeda Ollama"). O documento está certo;
a memória de topologia é de antes do provisionamento do job. Consequência prática: a **ponte de log
VM→Windows** (`docs/architecture/handoff-ponte-log-aimetrics.md`, itens 1-2) É necessária — o log
das gerações nasce em Linux e a API que o painel lê roda em Windows.

**How to apply:** ao planejar qualquer coisa de métricas de IA, tratar a VM como host de job de
produção (não mexer sem confirmação). Dois detalhes que só se descobrem por SSH:

1. `run-metrics-batch.sh` **não é versionado** (só existe na VM; o repo tem apenas
   `Scripts/vm/run-metrics-then-cypress.sh`, o wrapper Job1→Job2 ainda não instalado).
2. **Armadilha de case:** o script instalado passa `--log-dir "$APP_DIR/logs"` (minúsculo), mas o
   log Serilog existente (do teste `--limit 2`) está em `Logs/` (maiúsculo) e o wrapper versionado
   usa `Logs/`. Em Linux são diretórios distintos — antes de montar o `scp` da ponte, conferir para
   qual dos dois o cron realmente escreveu, senão a tarefa agendada copia um arquivo velho.

3. **A VM é ponto único de falha e já foi vista fora do ar:** em 2026-07-31 (véspera da rodada de
   sábado) ela ficou **totalmente inacessível** — sem ICMP, sem 22, sem 11434, e o `tracert` morre
   depois do gateway que responde normalmente pelo `.42`. Ou seja: VPN/rota OK, host fora. Se a VM
   não voltar antes de sábado 00:00, **não há rodada** — nenhuma ponte de log compensa isso.
   Consequência de desenho já aplicada: a tarefa de cópia no `.42` trata origem ausente como
   WARN + exit 0, nunca como erro permanente.

Ver [[runner-isolation-rollout]] e [[prod-42-acesso-bloqueado]].
