---
name: adr-layout-tree-425-connectus-decompile
description: Decompilação confirmatória do Connect Us (ilspycmd) para o endpoint de árvore dupla de layout (issue #425) — SysMiddle.Base.dll é o modelo real, GuidXPathCatalog já cobre 90%
metadata:
  type: project
---

Issue #425 (LayoutParserApi, bloqueia LayoutParserReact#267) pede endpoint que
devolva árvore de layout origem+destino (com cardinalidade e vínculos de
regra) para um mapeador Sysmiddle **específico** (mapperGuid já conhecido —
diferente de #417, que é descoberta/catálogo por workspace).

**Achado da decompilação exploratória** (`ilspycmd`, disponível em
`~/.dotnet/tools`, DLLs de referência em `D:\ConnectUs\Assemblies\`):
- `SysMiddle.MapControl.Core.dll` está **ofuscada** (nomes de classe
  ilegíveis, `[ObfuscationAttribute]`) e não tem tipos de layout — irrelevante.
- `SysMiddle.Base.dll` **não é ofuscada nos nomes de tipo/propriedade**
  (só os corpos de método vêm stripped) e contém exatamente o modelo que
  `ai/XslSynth.Contracts/Core/GuidXPathCatalog.cs` já parseia via XML puro:
  `ElementVO` → `WithChildrenElementVO` → `ParentOccurrenceVO` (com
  `MinimalOccurrence`/`MaximumOccurrence`, a cardinalidade que falta hoje) →
  `GroupTagElementVO`/`TagElementVO`/`AttributeElementVO`/`ChoiceElementVO`/
  `SequenceElementVO`. `LinkMappingItemVO` usa `InputLayoutGuid`/
  `TargetLayoutGuid` — os mesmos GUIDs que `sourceRefs`/`targetRefs` da
  `MappingExplanationController` (engine Sysmiddle) já expõem.
- Conclusão: **não existe superfície de dados paralela** (API in-process,
  cache, banco separado) por trás da árvore do Connect Us. É o mesmo arquivo
  LayoutVO exportado, o mesmo formato que `GuidXPathCatalog` já lê.

**Decisão registrada no ADR** (`docs/architecture/adr-layout-tree-endpoint-425.md`):
generalizar `GuidXPathCatalog` (extrair MinOccurs/MaxOccurs, integrar ao
lookup por GUID do banco em vez de path de arquivo local) em vez de
reimplementar. Ponto em aberto para `@lp-backend-dev`: `GuidXPathCatalog`
vive em `ai/XslSynth.Contracts` ([[xslsynth-trilha-a-overlap]], projeto
deliberadamente isolado) — decidir se migra para projeto compartilhado ou
duplica com testes de paridade; recomendação é compartilhado, não duplicar.

**Why:** o dono sugeriu decompilar só pra confirmar hipótese antes de
desenhar o endpoint — evitou reimplementar parsing que já existe e confirmou
que não tem nenhuma API/serviço externo escondido que precisaria ser
replicado.

**How to apply:** se qualquer tarefa futura tocar em "como o Connect Us
monta a árvore/cardinalidade/regra", a resposta já está aqui — não é preciso
decompilar de novo. Se o modelo mudar de versão do Connect Us, reconfirmar.
