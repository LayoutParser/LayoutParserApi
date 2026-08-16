---
name: project-catalog-warmup-single-shot-bug
description: Issue #67 (warm-up do catálogo sem retry) — CORRIGIDA de verdade em develop; o buraco que anulava o retry (falha de SQL virando "vazio") foi fechado em d608539, pendente de merge via PR #89
metadata:
  type: project
---

**Issue #67 — CLOSED e, diferente de #33/#51, com fix real no código** (verificado em 2026-08-15 em `origin/develop`: `RetryDelays` + `while (true)` presentes em `Services/Database/CachePermanentWarmupBackgroundService.cs`).

Histórico: o warm-up do catálogo rodava **uma única vez** após `app.Run()`. Blip transitório de SQL travava `CatalogWarmupState.LayoutCount` em 0, e `/health/ready` nunca se recuperava sozinho — só restart manual. Achado ao vivo pelo `@lp-devops` durante entrega de CodeQL/Dependabot (57 layouts só carregaram após restart); band-aid de infra (`Restart-Service` no smoke test do `ci-dev.yml`) foi mitigação, o fix real veio depois com backoff progressivo (5s, 15s, 30s, depois 60s fixo, sem teto de tentativas).

**Sequela ainda não mergeada:** o retry existia mas era **anulado** por `CachePermanentWarmupBackgroundService.cs:111` — `layoutCount = todos.Success ? todos.TotalFound : 0` convertia falha de leitura do SQL em "catálogo vazio", chamava `SetResult(0)` e retornava, matando o retry exatamente no cenário do blip de SQL. Corrigido em `d608539` (enum `CatalogWarmupStatus {Aquecendo, Pronto, Vazio}` + `RegisterFailedAttempt`), que em 2026-08-15 estava **na PR #89, ainda não em `develop`** (`RegisterFailedAttempt` ausente de `origin/develop`).

**Why:** o fix da #67 sozinho dava falsa sensação de resolução — o retry estava lá, mas inalcançável no caso de uso principal. Só a combinação #67 + `d608539` fecha o comportamento.

**How to apply:** se o usuário voltar falando de "/health/ready travado", "catálogo com 0 layouts" ou "Restart-Service no smoke test", **não abrir issue nova** — é a #67, já fechada. Antes de afirmar que está resolvido, checar se a PR #89 já foi mergeada (`grep RegisterFailedAttempt` no arquivo em `develop`/`master`); se não, o buraco do SQL ainda está aberto no ambiente implantado.

Related: [[project-backlog-nao-e-prova-do-codigo]], [[reference-gh-cli-setup]]
