---
name: pr209-falso-conflito-develop-stale
description: Investigação do suposto conflito entre PR #209 e develop no fix de IsDeclaredEmpty/best-effort (issue #140) — não havia conflito real, era ref local desatualizada
metadata:
  type: project
---

Em 2026-08-28 o `@lp-devops` reportou que a PR #209 (`feat/resolucao-estrutural-txt-xml-140` →
`develop`, pensada como docs-only) carregava um commit de código não mergeado
(`1992ed4`, fix best-effort para linha vazia/degradada) que conflitaria de verdade com
`develop` (que teria `99065a0`, uma reimplementação divergente do mesmo mecanismo baseada em
`IsNullOrWhiteSpace(currentLine)` cru em vez de campos de dado).

**Investigação (worktree limpo a partir de `origin/develop` fresco, `git fetch` antes):**

- `origin/develop` (tip real, `c1f3c1f`) **já contém** `1992ed4` como ancestral — foi trazido
  pelo merge da PR #205 (`92ff11f Merge pull request #205 from
  LayoutParser/feat/resolucao-estrutural-txt-xml-140`), que por sua vez já inclui a correção
  `07ce492 fix: IsDeclaredEmpty avalia campos de dado, nao a linha bruta` (esta corrige
  exatamente o bug de `IsNullOrWhiteSpace(currentLine)` cru — prefixo estrutural
  Sequencia/HEADER/EDI_/999999 torna esse cálculo inalcançável).
- O commit `99065a0` citado como "já mergeado em develop" **não é ancestral de
  `origin/develop`** — é ancestral só do branch-ref **local** `develop` deste clone, que estava
  desatualizado (parado em `c1f3c1f`... não, parado ANTES do merge da PR #205, em
  `99065a0`/`904739a`/`5ff66d0`). Ou seja, o "conflito" foi calculado comparando a PR #209
  contra uma cópia local obsoleta de `develop`, não contra o `origin/develop` real.
- `git merge-tree` da PR #209 (`pr209`, tip `3c06b6b`) contra `origin/develop` real: `merged`
  limpo, únicas mudanças são arquivos de memória/docs (README, `.claude/agent-memory/**`,
  `docs/architecture/**`) — exatamente o que a PR #209 promete ser (docs-only).
- Bônus: o próprio branch da PR #209 já tem um commit de memória (`3c06b6b docs(memory):
  registra PR #209 docs-only e decisao de comparar contra origin/develop`) reconhecendo essa
  mesma armadilha de ref local vs remota.

**Conclusão:** não há conflito de código real. `1992ed4` (best-effort) e a versão corrigida de
`IsDeclaredEmpty` (baseada em campos de dado, não linha crua) já estão consolidadas em
`origin/develop` via PR #205. Nenhuma branch de reconciliação de código foi necessária —
`fix/reconcilia-best-effort-issue-140` foi criada e removida sem commits, só para confirmar via
`git merge-tree`.

**Recomendação para `@lp-devops`:** ao reavaliar PR #209, rodar `git fetch origin` e comparar
contra `origin/develop` (não contra `develop` local) antes de declarar conflito. A PR #209 pode
seguir como estava planejada (docs-only) — não precisa ser fechada nem dividida. Vale, se
possível, `git branch -f develop origin/develop` (ou apagar a ref local obsoleta) neste clone
para evitar o mesmo engano em análises futuras — mas isso é operação de git local, não
regra de negócio; decisão de execução do devops.

Ver também [[issue-140-motor-resolucao-estrutural-implementado]] para o contexto do motor de
resolução estrutural que `1992ed4` protege (degradação Authoritative→BestEffort quando a linha
de origem é vazia/degradada).
