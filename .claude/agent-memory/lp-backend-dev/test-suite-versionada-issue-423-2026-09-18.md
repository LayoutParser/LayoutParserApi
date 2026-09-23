---
name: test-suite-versionada-issue-423-2026-09-18
description: Contrato de suite de teste versionada no Fiscal Test Lab (multiplas fixtures + historico) — issue #423, o que ficou dentro/fora do corte
metadata:
  type: project
---

Issue #423 implementada na branch `feat/fiscal-test-suite-423` (a partir de `origin/develop`,
que já tinha #421/#433/#438 mergeados). Commit `dda59b7`.

**Modelo escolhido:** `TestSuite`/`TestSuiteFixture`/`TestSuiteRun` (3 tabelas novas,
`tbTestSuite`/`tbTestSuiteFixture`/`tbTestSuiteRun`, `IdentityDatabase:*`) — FK só entre si,
SEM FK para `tbMappingDraft`/`tbMappingRelease` (mesma decisão de `SqlGeneratedMapperArtifactStore`
da #438: draft/release já são validados pela camada de aplicação antes de chegar no store).
`TestSuiteRun.FixtureResultsJson` guarda `TestSuiteFixtureResult[]` (passou/XSD válido/divergências
por fixture) — é o dado que sustenta o histórico pedido pela issue.

**Peça-chave reaproveitada:** extraí `IMappingTestRunService.EvaluateFixtureAsync` do corpo do job
de `EnqueueAsync` em `MappingTestRunService.cs` — é o mesmo núcleo síncrono (seleção xslt/tcl +
`EvaluateAsync` compartilhado: diff canônico + XSD + provenance) só que sem o wrapping de
`Task.Run`/`TestRunJobState`. `TestSuiteRunService` chama isso fixture-a-fixture. Não duplica NADA
do diff/XSD — só orquestra e agrega.

**Simplificação deliberada (documentada no XML doc de `TestSuiteRunService`):** `POST
.../test-suites/{suiteId}/run` é SÍNCRONO — devolve o resultado agregado direto no 200, sem job
pollável equivalente a `TestRunJobState`. Justificativa: cada fixture já é rápida (sem I/O
externo/Ollama) e o volume esperado de fixtures por suite é pequeno. Se isso mudar, migrar pro
padrão fire-and-forget é a evolução natural sem quebrar `ITestSuiteStore.RecordRunAsync`.

**Fora de escopo desta rodada (documentado no controller):** edição/remoção de fixture (só
create/list) e endpoint de comparação visual entre duas execuções — isso é
[[LayoutParserReact#204]], PM do front, não deste projeto.

**Endpoints** (`api/workspaces/{workspaceId}/mapping-drafts/{draftId}/test-suites`):
`POST`/`GET` (suite), `GET {suiteId}`, `POST {suiteId}/fixtures`, `GET {suiteId}/fixtures`,
`POST {suiteId}/run` (body `{releaseId}`), `GET {suiteId}/runs` (histórico paginado). RBAC
`RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)` nos 3 endpoints de escrita (create suite, add
fixture, run) — leitura só exige membership (mesmo padrão dos demais controllers fiscais).

Build 0 erros, 901 testes passando (`tests/LayoutParserApi.Tests`). Testes novos em
`TestSuiteRunServiceTests.cs` cobrem agregação mista (1 passa/1 falha → `RequiredGatesPassed=false`),
todas passando, suite sem fixture (lança sem persistir), release fora do workspace da suite.
