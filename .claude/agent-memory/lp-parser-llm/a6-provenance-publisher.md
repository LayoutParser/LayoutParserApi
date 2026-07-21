---
name: a6-provenance-publisher
description: A6 implementado 2026-07-21 (ProvenancePublisher) — cobre LinkMapping+DslRule, remove XComment de debug antes de "publicar"; achado de que CandidateBuilder tem o MESMO anti-padrão, não só LinkMappingTranspiler; ainda não rodou contra dado real nesta máquina
metadata:
  type: project
---

A6 (escopo corrigido pela Aria 2026-07-21) implementado em
`ai/XslSynth/Core/ProvenancePublisher.cs`, ligado ao Passo 7 de `RunRealAsync`
no `Program.cs` do XslSynth. Ver [[guid-xpath-catalog-a3]] (mesmo subsistema).

**Achado que amplia o escopo do bug original:** o pedido falava só do XComment
de debug do `LinkMappingTranspiler` (`target=... input=... xpath=...`), mas o
`CandidateBuilder` tem o MESMO anti-padrão nos nós de regra (`rule='...'
src=...`) — comentário literal filho do elemento de resultado, que sobrevive a
`XslCompiledTransform.Transform()` do mesmo jeito. `ProvenancePublisher.
StripDebugComments` remove os dois genericamente (todo `XComment` descendente),
sem precisar de 2 fixes especializados — decisão deliberada de não depender do
FORMATO do comentário.

**Decisão de design:** o sidecar é montado a partir dos objetos de domínio
ESTRUTURADOS (`LinkMappingItem`, `RuleTranslation`/`MapperRule.ContentValue`
via regex `I.LINHAnnn/campo`), não reparseando o texto do XComment de volta —
mais robusto, e desacopla o sidecar de qualquer mudança futura no formato do
comentário de debug (que continua existindo no candidato "de debug" — só a
cópia "publicável" fica limpa).

**⚠️ NÃO validado contra dado real ainda:** esta máquina/sessão não tinha
`.claude/tmp/export/MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.decrypted.xml` nem
`Documentos/Layout/layout-nfe.xml` (nenhum dado de sessão anterior estava
presente — ver [[session-environment-gotchas]]). Validado com MapperVO
SINTÉTICO construído à mão (2 LinkMappings — 1 resolvido por catálogo GUID tipo
CHAVEACESSO, 1 símbolico — + 2 Rules — 1 via DslBlockInterpreter, 1 stub
Untranslated): sidecar com as 4 entradas esperadas, 5 XComment removidos, 0
remanescentes. **Antes de considerar A6 fechada de verdade, rodar
`dotnet run` (sem args) numa máquina com o mapeador real descriptografado** e
conferir `candidate.published.xslt` + `generated-provenance.json` de verdade.

**How to apply:** ao retomar A6 ou qualquer trabalho em `LinkMappingTranspiler`/
`CandidateBuilder`, lembrar que QUALQUER novo `XComment` de debug que algum
código futuro adicionar já é coberto pelo strip genérico — não precisa
atualizar `ProvenancePublisher` a cada novo tipo de comentário.
