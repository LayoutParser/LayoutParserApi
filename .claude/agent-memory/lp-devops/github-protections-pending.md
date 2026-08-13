---
name: github-protections-pending
description: Estado da branch protection do GitHub (master/develop) — configurada em 2026-08-12
metadata:
  type: project
---

**Atualizado 2026-08-12:** branch protection foi aplicada em `master` e `develop` via
`gh api -X PUT .../branches/{branch}/protection`, autorização explícita do dono.
Antes da mudança nenhuma das duas tinha proteção (`protected: false`, sem config prévia
a preservar — confirmado via `gh api repos/LayoutParser/LayoutParserApi/branches`).

Config aplicada nas duas branches (idêntica):
- `required_pull_request_reviews`: `required_approving_review_count: 1`, `dismiss_stale_reviews: true`
- `enforce_admins: false` (admins ainda podem burlar — decisão consciente, não pedida explicitamente pelo dono, sinalizar se quiser endurecer)
- `allow_force_pushes: false`, `allow_deletions: false`
- `required_status_checks: null` (nenhum check obrigatório configurado — não há CI check status obrigatório amarrado ainda)
- `restrictions: null` (sem restrição de quem pode dar push além das regras de PR)

**Lacuna importante — NÃO resolvida pela config nativa:** a intenção do dono era
"master só recebe merge vindo de develop". A branch protection do GitHub **não tem**
mecanismo nativo para restringir a *branch de origem* de um PR — a config acima só
garante que `master` e `develop` exigem PR + 1 aprovação e bloqueiam push direto,
mas alguém ainda pode abrir PR de `feat/x` diretamente contra `master` (só não
conseguirá dar merge sem aprovação, mas o PR é permitido e pode ser aprovado por engano).
Para fechar 100% a intenção, precisaria de um workflow/Action que falhe o check quando
`base == master` e `head != develop`.

**Atualizado 2026-08-12 (sessão seguinte):** esse workflow **já existe** —
`.github/workflows/merge-gate.yml` (commit `d53de79`), job `verify-source`, roda em
`pull_request` contra `master`/`main` e falha se `head_ref != develop`. Confirmado rodando
com sucesso no PR #29 (develop→master, commit `9e821df`) — nome exato do check no GitHub:
`verify-source` (há também um check `build` no mesmo PR).

**Ainda falta:** anexar `verify-source` como `required_status_checks` na proteção de
`master` via `gh api -X PUT .../branches/master/protection` — sem isso o check roda e falha
visualmente mas **não bloqueia** o merge. Tentativa de executar esse PUT foi **bloqueada pelo
classificador de auto mode do Claude Code** (ação de alto risco em config remota) — precisa
rodar com o usuário presente/confirmando explicitamente, ou o próprio usuário executa. Comando
pronto (preserva `required_pull_request_reviews` com 1 aprovação + `dismiss_stale_reviews`,
`allow_force_pushes/deletions: false`) está registrado na resposta da sessão de 2026-08-12.

API_URL_PROD segue não existindo (nota antiga, ainda válida).
