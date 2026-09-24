---
name: field-correction-endpoint-issue-345-2026-09-08
description: Implementação do POST field-correction + tbFieldCorrectionContext/Report (issue #345), decisões de escopo tomadas sem violar o ADR
metadata:
  type: project
---

Endpoint `POST /api/transformation/field-correction` implementado em
`Controllers/TransformationExecutionController.cs`, branch `feat/endpoint-field-correction-345`
(a partir de `develop`, não pusheada — exclusivo de `@lp-devops`). Store novo
`IFieldCorrectionStore`/`SqlFieldCorrectionStore` (banco `IdentityDatabase:*`, nunca
`172.31.249.51`), registrado no `FiscalSchemaInitializer` (autossuficiente, sem FK contra o
resto do grafo fiscal).

**Duas decisões de escopo que não estavam 100% fechadas no ADR** (contrato-correcao-guiada-
humano, ver memória equivalente de `@lp-architect`) e que resolvi sem quebrar o desenho:

1. **Sem FK física entre `tbFieldCorrectionReport.DocumentId` e `tbFieldCorrectionContext.DocumentId`**
   — o controller já garante a existência do contexto (404 se ausente) antes de criar o
   reporte; integridade fica em aplicação, não em schema. Motivo: o ADR já prevê um cron
   futuro de TTL/retenção sobre o contexto — uma FK travaria essa limpeza depois.
2. **`MapperName` fica `null`** na primeira entrega — resolver exigiria uma consulta SQL
   adicional só para esse campo (não há essa informação disponível no ponto de
   `ExecuteTransformationCandidates` sem nova query), e é campo aditivo/best-effort no ADR,
   não bloqueante. `MapperGuid`/`GroundTruthXml` vêm normalmente do candidato sysmiddle
   quando existe.

**Why:** ambas reduzem acoplamento sem violar nenhuma garantia que o ADR exige (a Seção 4
do ADR já descreve a persistência como "best-effort" e "campo aditivo").

**How to apply:** se aparecer trabalho na Issue 2 (fila de curadoria, consumo no treino),
essas duas lacunas (FK ausente, MapperName null) são o primeiro lugar a revisitar — não são
bugs, são escopo deliberadamente cortado.

Persistência do contexto é fire-and-forget dentro de `ExecuteTransformationCandidates`
(`TryPersistFieldCorrectionContext`, mesmo padrão de `TryEnqueueAiCandidate`: `Task.Run` com
`IServiceScopeFactory` próprio, nunca atrasa a resposta síncrona). O endpoint de reporte em si
é síncrono/rápido (só valida + 1 leitura + 1 escrita no IdentityDatabase) — mas a
"correção" em si nunca reexecuta nada (ADR §3: nunca chama Ollama/RepairOrchestrator no
caminho do request).

11 testes novos em `tests/LayoutParserApi.Tests/Controllers/TransformationExecutionControllerFieldCorrectionTests.cs`
(fake `IFieldCorrectionStore` em memória — nunca toca SQL real), suíte completa 703/703 verde.
Comentado em `LayoutParserApi#345` e cross-repo em `LayoutParserReact#234` (ambos avisando que
não está em produção ainda).
