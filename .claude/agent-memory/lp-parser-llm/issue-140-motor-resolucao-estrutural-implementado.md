---
name: issue-140-motor-resolucao-estrutural-implementado
description: Itens 1/3/4/5 da issue #140 (XmlLayoutStructureParser sobre XSD real da NF-e, MappingKindClassifier, OccurrenceResolver, FieldToXmlMappingComposer) implementados e testados 2026-08-27
metadata:
  type: project
---

Implementação em `ai/XslSynth.Contracts/Core/StructuralResolution/` (branch
`feat/resolucao-estrutural-txt-xml-140`, commit `36ae5cb`), seguindo
`docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md`.

**Decisão do dono que desbloqueou o design (2026-08-27):** a fonte de verdade
da estrutura XML de destino é o **XSD da SEFAZ, por tipo de documento** — NF-e
por ora. Isso substitui a proposta original do design §2.1 (Sysmiddle
LayoutVO-first, XSD como fallback): aqui o XSD É o catálogo primário, não
fallback. Consequência: não reutilizei `GuidXPathCatalog.cs` (que resolve
`TargetGuid` Sysmiddle contra um LayoutVO exportado real, fora do Git) — é uma
peça diferente, para um caminho diferente (Sysmiddle-first), não superada nem
duplicada por este trabalho.

**XSD usado:** mirror `nfephp-org/sped-nfe`, pacote `PL_009_V4` (mesmo já
citado em `sefaz-xsd-schema-source`/`nt-pipeline-p1-p2-real-run`) — baixados
via `raw.githubusercontent.com` (rede disponível na sessão, `curl` funcionou
direto): `nfe_v4.00.xsd` (declara o elemento global `NFe`), `leiauteNFe_v4.00.xsd`
(complexType `TNFe`, a árvore inteira), `tiposBasico_v4.00.xsd`,
`xmldsig-core-schema_v1.01.xsd`. Copiados como fixture de teste em
`ai/XslSynth.Core.Tests/StructuralResolution/fixtures/` — é estrutura de
schema pública, não dado de documento, então não fere a regra de "nenhum dado
real de cliente".

**Peças entregues:**
- `XmlLayoutStructureParser` — usa `System.Xml.Schema.XmlSchemaSet` do BCL
  (resolve `xs:import`/`xs:include` sozinho via `XmlUrlResolver`) para
  compilar o XSD e percorrer os particles (`sequence`/`choice`/`all`) — não
  reimplementa parsing de XSD à mão. Produz `XmlLayoutNode` com `NodePath`
  sintético (caminho de nomes, não GUID Sysmiddle).
- `XmlLayoutCatalog` — índice por caminho completo e por nome de folha
  (ambíguo por design), builder de XPath absoluto com prefixo de namespace
  (`nfe:` fixo só para `http://www.portalfiscal.inf.br/nfe`; qualquer outro
  namespace gera `nsN` sob demanda).
- `MappingKindClassifier` — direct/transformed/concatenated/static sobre
  `StructuredRule` já existente (Camada 0/1), lista fechada de funções de
  concatenação (`ConcatString`).
- `OccurrenceResolver` + `FieldToXmlMappingComposer` — aplicam o critério
  binário `authoritative`/`best-effort` do design §5 (5 condições), sempre com
  `Limitations` preenchido quando `best-effort`.

**Gotcha real de XSD:** a NF-e tem `xs:choice` com elementos de MESMO nome em
ramos diferentes (ex.: `IPI` aparece 2x em `det/imposto` sob choices
distintos) — colisão de chave ao indexar por `NodePath` num `Dictionary`
ingênuo. Corrigido com `GroupBy(...).First()` em vez de `ToDictionary` direto.
Também `CFOP` (não só `vProd`) tem 2 ocorrências reais na árvore da NF-e —
testes de "nome de folha único" usam `natOp`, confirmado único por grep antes
de escrever o teste (não assumir unicidade de nome sem checar).

**Decisão de desacoplamento:** as classes novas NÃO dependem de
`Models.Entities` (runtime Windows-only da API) — recebem primitivos
(`TxtFieldReference` já é só GUID/nome/posição). Quem monta o
`MappingCandidate` (endpoint HTTP, item 6 do design, dono `@lp-backend-dev`)
faz a ponte com `ParsedField`/`LineElement`/`MapperVo` reais. Mantém o mesmo
princípio já documentado no `MapperVo.cs` de `XslSynth.Contracts`.

**Testado:** 25 testes novos (8 XSD real, 6 classificador, 11 composer) em
`ai/XslSynth.Core.Tests/StructuralResolution/` — 36/36 verdes no projeto
inteiro (sem regressão). `dotnet build LayoutParserApi.sln` completo: 0 erros,
637 warnings pré-existentes (SecurityCodeScan, nada novo introduzido).

**Pendente (fora do escopo desta tarefa, design §8 itens 2/6-9):** endpoint
HTTP que conecta `MappingStructureService`+`RealMapperParser`+`ParsedField`
reais ao composer; cache de `XmlLayoutCatalog` por `TargetLayoutGuid`
(`@lp-backend-dev`); validação comportamental contra `LowCodeRunner` real com
as 20 fixtures do design §6.1 (`@lp-qa`) — este trabalho só cobre o motor de
resolução (itens 1/3/4/5), não a comparação com o output real do runner.
