---
name: project-catalog-warmup-single-shot-bug
description: Issue #67 — CachePermanentWarmupBackgroundService roda uma única vez; blip transitório de SQL trava /health/ready Unhealthy permanentemente
metadata:
  type: project
---

`Services/Database/CachePermanentWarmupBackgroundService.cs` faz o warm-up do catálogo de layouts **uma única vez** após `app.Run()`. Se o SQL falhar nesse instante (mesmo transitório), `CatalogWarmupState.LayoutCount` fica travado em 0 e `CatalogHealthCheck`/`/health/ready` nunca se recuperam sozinhos — só restart manual do processo resolve.

Achado ao vivo pelo `@lp-devops` durante entrega de CodeQL/Dependabot (fora do escopo dele). Ele já mitigou com band-aid de infra (`Restart-Service` no smoke test do `ci-dev.yml`), mas o fix de verdade (retry/backoff no warm-up) é do `@lp-backend-dev`.

**Why:** deploy de dev expôs o comportamento ao vivo (57 layouts carregaram só após restart), então virou issue rastreável em vez de ficar só na mitigação de infra.

**How to apply:** issue #67 no Project #2 (Tipo=bug, Dono=lp-backend-dev). Se o usuário voltar falando de "/health/ready travado", "catálogo com 0 layouts" ou "Restart-Service no smoke test", essa é a issue já existente — checar status antes de abrir nova.

Related: [[reference-gh-cli-setup]]
