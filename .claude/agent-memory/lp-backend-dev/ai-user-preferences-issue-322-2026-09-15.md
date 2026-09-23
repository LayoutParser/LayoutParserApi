---
name: ai-user-preferences-issue-322-2026-09-15
description: Issue #322 (preferências de IA além do prompt) — dois stores paralelos coexistiam; decisão de onde as 3 preferências novas foram pousadas
metadata:
  type: project
---

Achado: `AiUserInstructionStore` (memória, `ConcurrentDictionary`) é o que o endpoint real
`ai-prompt-adicional` usa hoje — não sobrevive a restart. `SqlAiUserSessionStore`
(`tbLpAiUserSession`) já é persistido e já tinha `EnsureSessionAsync` gravando
`CustomPromptInstruction`, mas **nenhum controller chamava esse método** — confirmado: só
`AddHistoryEntryAsync`/`GetHistoryAsync` (histórico de tickets) estavam plugados no controller;
o parâmetro de prompt do `EnsureSessionAsync` era código morto.

Decisão tomada (issue #322): as 3 preferências novas (idioma, nível de detalhe da explicação,
engine padrão TCL/XSLT) nasceram direto em `tbLpAiUserSession` (3 colunas novas via ALTER
idempotente em `SqlAiUserSessionStore.SchemaDdl`), método `SetPreferencesAsync`/
`GetPreferencesAsync` novos — **não** reaproveitei `EnsureSessionAsync` (que já tinha upsert
parcial via `COALESCE`, mas só para o prompt). O prompt customizado existente **não foi
migrado** para o SQL — ficou como estava, só exposto junto na leitura de `GET ai-preferences`
(lido do `AiUserInstructionStore` em memória) para o front-end ter 1 chamada só.

**Why:** migrar o `ai-prompt-adicional` já em produção era risco/escopo maior do que a issue
pedia; o dono autorizou explicitamente deixar isso de fora se fosse grande demais.

**How to apply:** se uma issue futura pedir "unificar os dois stores de sessão de IA", esta é a
pendência exata — `AiUserInstructionStore` (memória) vs. `tbLpAiUserSession` (SQL, já com 4
colunas de preferência mas só 3 persistidas de fato via `SetPreferencesAsync`). Ver também
[[fiscal-profile-issue-379-2026-09-15]] para outro caso de concorrência na mesma working tree.

Endpoint: `PUT`/`GET api/transformation/ai-preferences`, `[Authorize]`, fail-closed 404 igual
ao padrão de `ReportFieldCorrection` (`CurrentUserId` vazio → 404), 422 para
`defaultTransformationEngine`/`preferredExplanationDetailLevel` fora de `tcl|xslt` /
`concise|detailed`. Consumo em runtime do `DefaultTransformationEngine` **não foi plugado** em
nenhum resolver — não achei um ponto único de decisão TCL-vs-XSLT ambíguo no código (grep por
"ambígu"/"EngineDefault" não achou nada); fica só persistido, documentado como decisão de
escopo, não regressão.
