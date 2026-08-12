---
name: github-protections-pending
description: Gates existem NO REPO (merge-gate verify-source, environment:production no deploy.yml) mas so viram BARREIRA via config do GitHub que o agente NAO aplica (gh ausente) — NAO enforced em 2026-08-11
metadata:
  type: project
---

Os workflows de gate existem no repositorio, mas dependem de configuracao do lado do GitHub
(branch protection / environments) para virarem barreira de verdade. Essa config NAO e derivavel
do repo e NAO pode ser aplicada daqui (gh ausente — ver [[env-gh-cli-ausente]]). Status em
**2026-08-11** (nenhum aplicado ainda; sao pendencia do dono do repo):

1. **`verify-source` como required status check na `master`** — o `merge-gate.yml` roda em todo PR
   que mira master e falha se a origem nao for `develop`, mas so BLOQUEIA o merge se for marcado
   como required status check na protecao da `master`. Ate la e apenas informativo.
2. **`environment: production` com required reviewer** — o job `deploy` do `deploy.yml` ganhou
   `environment: production` (commit 56940cd), mas o rotulo so vira portao de aprovacao depois que o
   environment `production` for criado no GitHub com **required reviewer = dono do projeto**. Sem
   isso o `environment:` so cria o label, nao bloqueia deploy nenhum.
3. **Branch protection na `master`** (branch default real do remoto) — PR obrigatorio + 1 review +
   o required check do item 1. Confirmado que a default e `master`.

**Variable opcional nova:** `API_URL_PROD` — o smoke test pos-deploy do `deploy.yml` deriva
`http://localhost:5000` quando ela nao existe (a instancia de prod serve na 5000). Criar so se a
validacao precisar apontar para outro host/porta. Nao existe hoje.

**Contexto historico (decai):** a PR #27 (develop->master) foi mergeada em 2026-08-11 ANTES de o
`deploy.yml` ter smoke test de readiness pos-deploy e ANTES de existir required reviewer. Se essa
merge disparou o deploy de prod, ele rodou SEM gate de readiness. Nao verificado (sem gh, sem acesso
ao .42 — ver [[prod-42-acesso-bloqueado]]).

**Why:** um gate que mora so no YAML nao protege nada sozinho — a metade que falta e config de UI.
Assumir que "o merge-gate/environment esta ativo porque o workflow existe" e o erro a evitar.

**How to apply:** antes de afirmar que a `master` esta protegida ou que um deploy de prod exige
aprovacao, lembre que NADA disso estava enforced em 2026-08-11; verifique o estado atual no GitHub
(browser ou de uma maquina com gh) em vez de inferir do repo.
