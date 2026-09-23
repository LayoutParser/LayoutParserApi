---
name: rbac-erro-diff-376-doc
description: Onde ficou a doc do contrato RBAC/erro/diff fiscal (#376) e o que reconciliar quando #377 mesclar
metadata:
  type: project
---

Issue #376 (branch `docs/rbac-erro-diff-mapping-376`, commit `8718a08`, base `develop` e1bb6bf):
publicada a doc dos 3 itens "fecham só documentando" do cross-check #226/#198.

- Doc dedicado: `docs/architecture/contrato-rbac-erro-diff-mapping-fiscal-2026-09-10.md`
  (matriz RBAC endpoint×papel, vocabulário de erro do `PATCH .../rules/{ruleId}`, shape do
  diff `NodeDiff`→`MappingTestRunDivergence` em `testRunSummary.divergences`).
- README: nova subseção **8.1** em §8, linkando o doc. Índice não mexido (subseções não entram nele).
- XML docs: `<remarks>` de RBAC em approve/publish/rollback/List (`MappingGovernanceController`)
  e `<remarks>` de status no handler `UpdateRule` (`MappingDraftsController`).

**Reconciliar quando #377 mesclar:** issue #377 (`feat/mapping-releases-filtros-377`) adiciona
filtros `status`/`draftId`/`environment` ao `GET .../mapping-releases`. A doc e o `<remarks>` do
`List` afirmam que **esses filtros não existem ainda** (verdade em `develop` no momento do commit).
Ao #377 entrar em `develop`, atualizar as duas menções.

**Processo:** o commit foi feito num `git worktree` isolado (`../LayoutParserApi-wt-376`) porque a
working tree principal estava ocupada por outro agente no branch `#377` — ver
[[concorrencia-git-worktree-isolado]]. Padrão a repetir quando a árvore principal estiver suja.
