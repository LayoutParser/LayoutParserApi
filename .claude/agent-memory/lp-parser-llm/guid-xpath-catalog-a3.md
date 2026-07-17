---
name: guid-xpath-catalog-a3
description: A3 (GuidXPathCatalog) implementado 2026-07-17 — LayoutVO real do Connect Us resolve 235/237 LinkMappings; gotchas de encoding/whitespace do export
metadata:
  type: project
---

A3 do plano `multi-session-execution-plan.md` §8.2 implementado em
`ai/XslSynth/Core/GuidXPathCatalog.cs`, ligado ao `LinkMappingTranspiler` e a
`Program.cs` (`RunRealAsync`) — ver [[poc3-r4-estado]] e [[multi-client-mappers]].

**Achado central:** o arquivo `Documentos/Layout/layout-nfe.xml` (dentro do
`actions-runner/_work` do servidor, caminho completo em
`.claude/tmp/servidor/layoutparser/actions-runner/_work/LayoutParserApi/LayoutParserApi/Documentos/Layout/`)
é o LayoutVO REAL exportado do Connect Us — seu `<LayoutGuid>`
(`LAY_767be1dd-…`) bate EXATAMENTE com o `TargetLayoutGuid` do mapeador SEND_ENV
real (`MAP_f31a6758-…`). Não é fixture, é produção. `layout-mqseries.xml` do
mesmo diretório é uma estrutura ANÁLOGA (FieldElementVO/LineElementVO) mas com
`LayoutGuid` DIFERENTE do `InputLayoutGuid` do mapeador (`LAY_ad4fb6f4-…`) — ou
seja, o layout de ENTRADA exato não está disponível localmente (bloqueia A4
até vir do Connect Us).

**2 gotchas reais de parsing do LayoutVO** (sem eles o catálogo resolvia só
1/237 GUIDs, quase inútil):
1. Mesma pegadinha do MapperVO: o arquivo declara `encoding="utf-16"` no
   prólogo mas os bytes são UTF-8 (com BOM) — reusar
   `RealMapperParser.DecodeAndFixDeclaration` antes de `XDocument.Parse`.
2. **Pretty-print profundo quebra o TEXTO dos elementos**: `<ElementGuid>` e
   `<Name>` vêm indentados a 100+ colunas, e o exportador quebra linha DENTRO
   do valor (`<ElementGuid>\n    TAG_af34…</ElementGuid>`). Sem `.Trim()` no
   `.Value`, a chave do dicionário carrega newlines/espaços embutidos e NUNCA
   bate com o GUID limpo do `MapperVO` (que não tem esse problema — seu XML é
   compacto). Corrigido: `Trim()` em `ElementGuid`/`Name`/`xsi:type` no
   `GuidXPathCatalog.Caminha`.

**Estrutura do LayoutVO** (por `xsi:type`): `GroupTagElementVO`/`TagElementVO`
contribuem um segmento de XPath; `AttributeElementVO` contribui `@Name` no
XPath do pai; `ChoiceElementVO`/`SequenceElementVO` são wrappers PUROS
(`Name` literal "Choice"/"Sequence") — não entram no XPath, só repassam o
caminho do pai aos filhos (mesma convenção do `xs:choice`/`xs:sequence` em
`XsdLeiauteIndex`).

**Resultado real (mapeador FIAT SEND_ENV):** 1098/1108 GUIDs únicos do
LayoutVO resolvidos; 235/237 `LinkMappings` resolvidos pelo catálogo (os 2
restantes têm `TargetGuid` prefixo `ATT_` — destino ATRIBUTO, excluído por
design: emissão de atributo precisa de forma XSLT diferente da folha-elemento,
fora do escopo de A3).

**Why:** sem isso, o `select` do input em todo LinkMapping era SIMBÓLICO
(token do GUID) — o XSLT compilava mas não casava com nenhum input real.
**How to apply:** ao trabalhar com QUALQUER export do Connect Us (LayoutVO ou
similar) que pareça "quase vazio" ao parsear com `XDocument` puro, suspeitar
primeiro do encoding-lie E do whitespace embutido por pretty-print — não do
conteúdo em si.
