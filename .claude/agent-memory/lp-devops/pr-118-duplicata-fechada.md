---
name: pr-118-duplicata-fechada
description: PR #118 era duplicata de #117 (mesmo branch fix/deploy-rollback-automatico) mirando master direto; fechada, nao mergeada.
metadata:
  type: project
---

Em 2026-08-15, PR #118 (`fix/deploy-rollback-automatico` -> `master`) foi aberta em paralelo
a PR #117 (mesmo branch -> `develop`, corretamente). #117 foi mergeada primeiro
(merge commit `9a2c197`). Quando o dono pediu "mergeia a PR #118 quando sair", a checagem antes
do merge (`gh pr edit 118 --base develop`) falhou com "no new commits between base branch
develop and head branch" — sinal de que o branch ja estava 100% contido em `develop`.

**Decisao:** fechar #118 sem merge (não reabrir/redirecionar), pois o conteúdo já está em
`develop`. Mergear #118 em `master` teria pulado o fluxo `fix/* -> develop -> master` (ver
[[github-protections-pending]] — o merge-gate hoje só é convencional, não bloqueia
tecnicamente, então essa checagem manual é a única defesa real).

**Como aplicar:** antes de mergear qualquer PR de `fix/*`/`feat/*` direto contra `master`,
verificar primeiro se já existe uma PR irmã do mesmo branch contra `develop` já mergeada
(`gh pr list --head <branch> --state all`). Se sim, a PR contra `master` é lixo/duplicata —
fechar, não mergear. O passo `develop -> master` é sempre uma PR separada, dedicada, não a
mesma PR do fix.
