---
name: board-sync-2026-09-01
description: 5 issues fechadas (#226,#227,#231,#232,#94) dos 7 slices da plataforma fiscal — código já mergeado em develop mas issues seguiam OPEN.
metadata:
  type: project
---

Fechei via `gh issue close --comment`, cada uma citando o PR de evidência, após confirmar
`state == MERGED` com `gh pr view <n> --json state,mergedAt` para as 4 PRs envolvidas:

- #226 + #227 (Slice 4, explicabilidade TCL/XSL) — PR #240, mergeado 2026-08-31.
- #231 (Slice 5, compilação TCL/XSL/XSLT + Fiscal Test Lab) — PR #243, mergeado 2026-09-01.
- #232 (Slice 6, gate transversal Sysmiddle) — PR #247, mergeado 2026-09-01.
- #94 (Slice 7, governança de mapeadores) — PR #248, mergeado 2026-09-01.

**Why:** mesmo padrão de [[project_board-sync-2026-08-28]] — issues implementadas ficam OPEN
depois do merge quando o fechamento automático via closing keyword não acontece (aqui era o caso
comum de 1 PR resolvendo 2 issues numeradas, `#226/#227`, ou simplesmente ninguém ter usado
`Closes #N` no corpo do PR).

**How to apply:** antes de fechar qualquer lote de issues "supostamente resolvidas por PR
mergeado", sempre rodar `gh pr view <n> --json state,mergedAt` pra cada PR citado — não confiar
na alegação de quem pediu o board-sync. Só fechar as que confirmarem `MERGED`; reportar
explicitamente quais ficaram pendentes e por quê (aqui não houve nenhuma pendente, todas as 4
PRs bateram).
