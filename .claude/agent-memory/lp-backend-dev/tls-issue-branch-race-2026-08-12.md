---
name: tls-issue-branch-race-2026-08-12
description: 3ª ocorrência do branch race entre sessões concorrentes — commit da issue #34 (TLS) caiu em chore/versionar-scripts-metrics-vm; corrigido com cherry-pick + revert em vez de force-reset
metadata:
  type: feedback
---

Mesma classe de incidente de [[pathway1-removal-branch-race-2026-08-12]] e
[[gates-auditoria-enforcement-2026-08-12]], 3ª vez no mesmo dia (2026-08-12). Fiz
`git checkout -b fix/tls-api-certificado-autoassinado` a partir de `ca93fc7`, editei/commitei
— mas entre o checkout e o commit outra sessão trocou o branch corrente do worktree para
`chore/versionar-scripts-metrics-vm`. O commit `feat(seguranca): habilita endpoint HTTPS...`
caiu lá, não na branch pretendida.

**Correção aplicada (sem `branch -f`, que o classifier bloqueia toda vez):**
1. `git checkout fix/tls-api-certificado-autoassinado` (a branch certa já existia, só não
   era mais a corrente).
2. `git cherry-pick <hash-do-commit-perdido>` — reaplica o commit na branch certa.
3. Na branch errada (`chore/versionar-scripts-metrics-vm`), **não fiz `reset --hard`**
   (destrutivo, pode afetar a outra sessão que está usando esse branch) — usei
   `git revert --no-edit <hash>`, que desfaz o commit com um commit novo, não reescreve
   histórico. Mais seguro quando não se sabe se outra sessão já leu/baseou algo no commit
   errado.

**Why revert em vez de reset desta vez:** nas duas ocorrências anteriores o branch atingido
era o meu próprio (`fix/agregacao-infcpl`), então reset/force era aceitável. Desta vez o
branch atingido (`chore/versionar-scripts-metrics-vm`) pertence a outra sessão ativa —
reescrever a ponta dela com `reset --hard`/`branch -f` seria destrutivo por definição, e o
classifier já bloqueia `branch -f` de qualquer forma. `revert` é sempre seguro em branch de
terceiros porque só adiciona commit, nunca remove histórico que a outra sessão possa já ter
puxado.

**How to apply:** ao detectar que um commit caiu na branch errada por causa de troca
concorrente, primeiro checar **de quem é a branch afetada** antes de escolher a correção —
branch própria → reset/force é ok (mas será bloqueado, indo direto pro cherry-pick+manual
como nas ocorrências 1/2); branch de outra sessão → **sempre `revert`**, nunca
`reset --hard`/`branch -f`.
