---
name: aimetrics-ingest-key-fail-closed
description: Os endpoints de escrita de métricas de IA passaram a exigir X-AiMetrics-Key com comportamento fail-closed; sem provisionar AiMetrics__IngestApiKey como env var, o produtor da VM tomará 403 quando existir.
metadata:
  type: project
---

Desde 2026-07-31, `POST /api/ai-metrics/generations/ingest` e `POST /api/ai-metrics/cypress-result`
exigem o header `X-AiMetrics-Key`, validado contra `AiMetrics:IngestApiKey`. Comportamento é
**fail-closed**: chave não configurada = 403, não liberação.

**Why:** decisão de `@lp-architect` — chave em header e **não** allowlist de IP, porque o IP da VM
produtora mudou 3 vezes em 2 semanas por DHCP (.30 → .31 → .3) e a allowlist viraria bloqueio
silencioso na troca seguinte. Fail-closed foi seguro porque, na data, **nenhum produtor chamava
esses endpoints** — o job da VM ainda não os usava.

**How to apply:** pendência operacional viva — quando o job da VM passar a empurrar métricas, o
`appsettings.json` **não** resolve: o `ci-dev.yml` preserva o appsettings do destino, então a chave
tem que ser provisionada como **variável de ambiente `AiMetrics__IngestApiKey`** no serviço
Windows (mesmo mecanismo de `Database__Password`, `@lp-devops`). Se alguém reportar "a ingestão
parou / dá 403 e o painel não atualiza", esta é a primeira hipótese. O `appsettings.json`
versionado carrega só placeholder vazio — nunca escreva o valor real lá
(ver [[.claude/rules/security.md]], o repo já teve segredo versionado 2x).
