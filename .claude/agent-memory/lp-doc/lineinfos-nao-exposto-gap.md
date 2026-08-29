---
name: lineinfos-nao-exposto-gap
description: ParsingResult.LineInfos (IsDeclaredEmpty/PositionalAlignmentFailed) é preenchido internamente pelo parser mas POST /api/parse/upload não o serializa na resposta
metadata:
  type: project
---

Confirmado em código (2026-08-27, durante documentação da PR #198,
`feat/contrato-linha-vazia-e-progresso`): `ParsingResult.LineInfos` é populado em
`Services/Implementations/LayoutParserService .cs` (dentro de
`ParseTextWithSequenceValidation`) com os dois sinais aditivos do contrato
(`LineInfo.IsDeclaredEmpty`, `LineInfo.PositionalAlignmentFailed`,
`Models/Entities/LineInfo.cs`). Porém `Controllers/ParseController.cs` — no `Ok(new {...})`
de sucesso do `Upload` (~linha 303-322) — **não referencia `result.LineInfos`** em nenhum
campo do payload. Ou seja, os dois sinais existem no back-end mas hoje **não chegam ao
front** por esse endpoint.

**Why:** ao documentar o contrato aditivo pedido (README + XML docs), grep confirmou que não
há `lineInfos`/`LineInfos` em lugar nenhum do controller — só na entidade e no service. Isso
diverge do que a demanda original descrevia ("front-end deve usar isso"), então documentei o
gap explicitamente (README §4 + XML doc em `Models/Parsing/ParsingResult.cs`) em vez de
descrever como se já estivesse exposto — regra do projeto é "verdade > marketing", não
documentar como pronto o que ainda não está.

**How to apply:** antes de fechar a PR #198 como "front pode consumir `IsDeclaredEmpty`/
`PositionalAlignmentFailed`", `@lp-backend-dev` precisa adicionar `lineInfos` (ou equivalente)
ao objeto de resposta de `POST /api/parse/upload`. Enquanto isso não acontecer, qualquer
comunicação ao front sobre esses campos deve deixar claro que ainda não estão no payload real.
