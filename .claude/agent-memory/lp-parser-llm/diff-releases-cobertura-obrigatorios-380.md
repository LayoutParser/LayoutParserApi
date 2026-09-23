---
name: diff-releases-cobertura-obrigatorios-380
description: Issue #380 (#198.2b diff release×release + #198.5 cobertura de obrigatórios) — dados que não existiam precisaram ser adicionados ao modelo; leaf-only semantics do XSD.
metadata:
  type: project
---

Issue #380 (branch `feat/diff-releases-cobertura-obrigatorios-380`) implementou dois endpoints em
`MappingCompilationController`:

- `GET .../releases/diff?fromReleaseId=&toReleaseId=` (#198.2b): diff canônico via
  `CanonicalDiffer` entre os XMLs REAIS produzidos por duas releases (não contra o gabarito).
- `requiredCoverage` no `GET .../releases/{releaseId}` (#198.5): % de destinos obrigatórios do XSD
  cobertos pelas regras accepted/edited da release.

**Achado real, não previsto no plano:** `MappingTestRunSummary` (persistido como JSON blob) NUNCA
guardava o XML de saída real do test-run — só as divergências contra o gabarito. Sem isso, "diff
entre os XMLs produzidos por duas releases" era impossível de implementar honestamente. Resolvido
adicionando `ActualXml`/`ExpectedXml` (nullable, trailing default) ao record e persistindo em
`MappingTestRunService.RunXsltTestAsync`. Como é um blob JSON (`TestRunSummaryJson`), não precisou
de migration — mas é um precedente: releases antigas (test-run rodado antes desta issue) têm
`ActualXml=null` e o diff cai no 422 "sem test-run executado" mesmo tendo passado no gate antigo.

**`RequiredCoverageCalculator`** (novo, `Services/Fiscal/`) enumera elementos/atributos obrigatórios
via `XmlSchemaSet` andando recursivamente pelo `ContentTypeParticle`. Decisão de design não-óbvia:
só conta como "destino obrigatório que precisa de TargetRef" os elementos FOLHA (sem filhos
complexos) + atributos `use=required` — containers estruturais (`<infNFe>`, `<ide>`) são sempre
emitidos pela estrutura do XSLT e NUNCA precisam de regra própria. Sem essa distinção os testes
davam 60% em vez de 100% mesmo cobrindo todos os valores reais — o "obrigatório" tem que ser
value-level, não estrutural. `<xs:choice>` é tratado conservador: nenhum ramo é forçado obrigatório
(um OU outro satisfaz o schema).

**Reuso confirmado, sem duplicar:** `FiscalProfileResolver.Resolve` (issue #379) para achar
`resolvedXsd`; `XsdValidationService.FindXsdFile` (privado, mesma classe) via novo método público
`TryLoadSchemaSet` — não reinventou o parser de XSD.

Gotcha de teste: `XsdValidationService` é classe concreta (não interface) com 4 deps de construtor —
sem Moq no projeto, os testes de controller constroem uma instância real com `IConfiguration` em
memória apontando pra um dir temp (`XsdValidation:BasePath`); quando o XSD não existe no disco,
`TryLoadSchemaSet` degrada pra `null` graciosamente — usado deliberadamente nos testes que não
exercitam `FiscalProfile` (não precisa mockar).
