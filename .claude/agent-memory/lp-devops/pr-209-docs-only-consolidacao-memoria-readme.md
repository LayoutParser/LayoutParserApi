---
name: pr-209-docs-only-consolidacao-memoria-readme
description: PR #209 (feat/resolucao-estrutural-txt-xml-140 -> develop) publica memorias de agente e reconciliacao do README acumuladas apos a cadeia #198-#207; codigo ja estava em develop via PR #205.
metadata:
  type: project
---

Branch `feat/resolucao-estrutural-txt-xml-140` teve o codigo mergeado via PR #205 (issue #140),
mas ficaram 4 commits locais sem push: 3 de auditoria/memoria (`27b49f8`, `11a30e7`, `dc4ef79`)
mais 1 criado nesta sessao (`a167567`) consolidando memorias adicionais de `@lp-doc`/`@lp-pm`
(reconciliacao do README, board-sync 2026-08-28 fechando 5 issues manualmente).

**Decisao:** em vez de cherry-pick pra branch nova, abri PR direto desta branch contra `develop` —
`git log develop..HEAD` (branch local `develop`, desatualizada) mostrava dezenas de commits de
codigo, mas isso era ilusao de `develop` local estar defasada. O diff real (`git merge-base HEAD
origin/develop` -> HEAD) confirmou 100% documentacao/memoria (25 arquivos), sem tocar codigo-fonte.
Licao: **sempre comparar contra `origin/<branch>`, nunca a ref local**, antes de decidir se um PR
"parece" ter codigo misturado.

**PR:** #209 (https://github.com/LayoutParser/LayoutParserApi/pull/209), base `develop`,
head `feat/resolucao-estrutural-txt-xml-140`. Build verde antes do push (0 erros). Sem segredos
nos arquivos novos (checado via grep por password/apikey/token/connectionstring). Merge fica com
o dono.

**How to apply:** se aparecer de novo uma branch de feature com "sobras" de commits de doc/memoria
depois do merge do codigo principal, o playbook e: (1) `git fetch origin`, (2) comparar com
`git merge-base HEAD origin/<base>` em vez de ref local, (3) se o diff for so docs/memoria, PR
direto da branch existente e mais simples que cherry-pick.
