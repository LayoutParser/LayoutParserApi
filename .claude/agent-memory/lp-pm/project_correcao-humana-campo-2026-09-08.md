---
name: correcao-humana-campo-2026-09-08
description: Issues #345/#346 do ADR de correção guiada por humano (LayoutParserReact#232/#234)
metadata:
  type: project
---

Formalizadas 2 issues a partir de `docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md`
(branch `docs/adr-contrato-correcao-humana`, não mergeada em `develop` na data da formalização):

- **#345** — endpoint `POST /api/transformation/field-correction` + `DocumentId` novo em
  `TransformationExecutionCandidatesResponse` + `tbFieldCorrectionContext`/`tbFieldCorrectionReport`
  (`IdentityDatabase:*`). Dono natural: `@lp-backend-dev`. Marcada com destaque: o `DocumentId`
  é o item que bloqueia `LayoutParserReact#234` — sugerido como possível sub-entrega isolada.
- **#346** — fila de curadoria (`pending`/`reviewed_accepted`/`reviewed_rejected`) antes do
  reporte alimentar o JSONL de treino incremental (F3/#338). Depende de #345. Dono natural:
  `@lp-parser-llm`.

Comentado em `LayoutParserReact#232` e `#234` linkando ambas.

**Why:** correção humana não tem o mesmo critério "1:1 automático" do `RepairOrchestrator`
(usuário pode discordar do Sysmiddle) — por isso o ADR já recomendou separar em 2 issues em
vez de uma.

**How to apply:** se `@lp-backend-dev`/`@lp-parser-llm` pedirem mais detalhe de critério de
aceite, a fonte é o ADR, não esta memória (esta é só o índice de rastreio).
