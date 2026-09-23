---
name: board-sync-2026-09-03
description: Fechamento de issues 90/104/173/110 apos PRs #295-#300 mergeadas em develop sem closing keyword; 219/88/97 mantidas abertas.
metadata:
  type: project
---

PRs #295-#300 mergeadas em `develop` (2026-09-03) sem `Closes #N` — precisou de board-sync manual
lendo cada PR/commit contra o criterio de aceite da issue.

Fechadas (evidencia verificada no diff real do commit merge, nao so na descricao da PR):
- #90 (gate DI Program.cs) via PR #298/commit 339c194 — `ProgramCompositionRootTests.cs` sobe
  `WebApplicationFactory<Program>` real, cobre o alvo da mutacao M4 (hosted service) + 9 grupos.
- #104 (E2E TryEnqueueAiCandidate) via PR #299/commit fdf37f8 — `FakeSysmiddleRunner` sem reflection.
- #173 (validacao TCL) via PR #296/commit b3b7d3b — `TclStructureValidator` + 8 testes.
- #110 (dry-run config drift) via relatorio ja postado na issue — criterio nao exigia
  literalmente workflow_dispatch isolado, so "relatorio lido + hipotese confirmada/refutada".

Mantidas abertas (codigo != validacao real):
- #219 (layout FIAT tipo 2) — PR #295 so normaliza em codigo; cadastro real no banco
  (`ad4fb6f4-...`, LayoutType="2") nao foi corrigido, e o criterio original exige o gate FIAT
  real passar, que so se confirma rodando o Cypress/Pollux — fora do alcance do agente.
- #88, #97 — ja tinham comentario de progresso preciso de outro agente (PR #300 e #297
  respectivamente), nao fechadas por decisao pendente do dono (parcial/duplicata); nao duplicado.

**Why:** reforca o padrao ja visto em [[project_board-sync-2026-08-28]] — closing keyword nunca
atravessa merge sem `Closes #N` explicito na PR, e "PR implementa X" != "criterio de aceite
cumprido", especialmente quando o criterio pede validacao em ambiente real fora do alcance do agente.
**How to apply:** ao fazer board-sync pos-merge, sempre ler o diff real do commit (nao so a
descricao da PR) e comparar item a item contra o checklist original da issue antes de fechar.
