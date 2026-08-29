---
name: issue141-fieldmappings-execute-candidates-qa-gate
description: QA gate da issue #141 (fieldMappings em execute-candidates) — PASS; achado sobre Compose() não filtrar por resolução de origem; overhead isolado medido em ~0.1ms p95, real p95 do runner .exe inacessível em Linux
metadata:
  type: project
---

Issue #141/React#128, branch `feat/fieldmappings-execute-candidates-141` (worktree
`/mnt/c/Users/elson.lopes/source/repos/LayoutParserApi-wt-141`), commit da implementação `ed8f0bb`.
Veredito: **PASS** (build limpo, 408/412 testes — 4 falhas pré-existentes Windows×Linux, ver
[[unified-logging-parse-bug-and-log-dir-incident]] linha de raciocínio similar de baseline).

**Achado (não bloqueador):** `FieldMappingCompositionService.Compose()` não filtra entradas por
resolução de origem — um `LinkMappingItem` cujo `InputLayoutGuid` não existe no parse ainda gera
uma entrada `FieldToXmlMapping` com `Confidence: BestEffort`. `fieldMappings: []` só ocorre quando
o mapper não tem nenhum `LinkMappingItem`/`Rule` para iterar, não quando nenhum resolve contra o
documento. O design da #141 assumia implicitamente "sem correspondência → []" — não é verdade.
Escrevi o teste de contrato para isso comprovando o comportamento real, não o assumido.

**Performance:** p95 real do endpoint com o `LowCodeRunner.exe` é IMPOSSÍVEL de medir neste
ambiente (Linux/WSL, runner é x86 nativo Windows) — mesmo bloqueio de sempre, ver
[[cypress-alpha-emissao-normal-spec]]. Medi em vez disso o overhead ISOLADO do código novo (parse
compartilhado + RealMapperParser + Compose) via microbenchmark descartável (nunca commitado):
com cache de catálogo XSD FRIO (recriado por iteração, replicando compilar o XSD do zero), o
overhead aparente é de ~374ms p95 — **isso é ruído de setup, não do código de produção**. Com
cache quente (controllers reusados entre iterações, replicando o singleton real do
`StructuralXmlCatalogCacheService`), o overhead cai para ~0.11ms p95 — consistente com a hipótese
do design (custo desprezível frente ao runner, que domina com centenas de ms-segundos). Lição
para replicar: **sempre medir microbenchmarks de composição com o cache de catálogo XSD quente**
(reusar a instância do serviço entre iterações) — cache frio infla o número artificialmente e não
representa o request real.

**Recomendação:** não implementar o cache do §4 do design no dia 1 — margem de segurança grande
frente ao requisito de ≤10% de regressão. Cache fica condicional a medição futura com o runner
real (bloqueio de ambiente, não de decisão).

Resultado completo documentado em `docs/architecture/design-contrato-fieldmappings-execute-candidates-issue-141.md` §9.
2 testes de contrato adicionados em `tests/LayoutParserApi.Tests/Controllers/TransformationExecutionControllerFieldMappingsTests.cs` (commit `98b527e` na branch `feat/fieldmappings-execute-candidates-141`).
