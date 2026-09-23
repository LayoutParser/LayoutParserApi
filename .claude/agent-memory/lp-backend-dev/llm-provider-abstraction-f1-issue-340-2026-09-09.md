---
name: llm-provider-abstraction-f1-issue-340-2026-09-09
description: Implementação de ILlmProvider/LlmProviderResolver (F1, issue #340) — onde ficam os call-sites migrados e o que fica fora de escopo por design
metadata:
  type: project
---

Issue #340 (F1 do ADR `docs/architecture/adr-llm-provider-plugavel-2026-09-08.md`) implementada em
`feat/llm-provider-abstraction-340` (commit `ba3a914`, local, a partir de `develop`). Criou
`Services/Llm/{ILlmProvider,DataSensitivity,LlmProviderResolver,OllamaLlmProvider}.cs` e migrou
`OllamaValidationDiagnosticService` + `MappingSuggestionService` pra consumir a abstração em vez de
`HttpClient`/`OllamaOptions` diretos.

**Por quê isso importa pra próximas issues (F2 em diante):** o guard de `LlmProviderResolver.Resolve`
(recusa `InvalidOperationException` se `sensitivity == RealFiscalDocument` e `provider.Locality ==
Cloud`) já está ativo e testado (`tests/.../Services/Llm/LlmProviderResolverTests.cs`), mesmo sem
nenhum provider `Cloud` real existir ainda — é o mecanismo que a issue #341 (F2, gap de proveniência)
e depois F3 (primeiro provider de nuvem) vão depender. Não precisa ser refeito.

**O que ficou de fora por decisão do próprio ADR, não por corte meu:**
- `ai/XslSynth.Core/Synthesis/OllamaClient.cs`/`OllamaXslSynthesizer.cs` — subprojeto standalone,
  ciclo de vida diferente, ADR §3.2 explicitamente pede pra não tocar nesta fase.
- `MappingSuggestionService`/`SyntheticDataGeneratorService` continuam classificados
  `DataSensitivity.RealFiscalDocument` mesmo sem 100% de certeza de que todo artefato é real — é a
  postura conservadora do ADR §2.3 até existir campo de proveniência (isso é o trabalho da F2).

**Detalhe de design que não estava no ADR e eu tive que decidir:** `LlmRequest.JsonSchema` é texto
bruto (string) que o `OllamaLlmProvider` faz `JsonDocument.Parse` e embute como valor literal do
campo `format` do payload Ollama — permite tanto um schema objeto completo (usado pelo diagnóstico
de validação) quanto o literal `"json"` (usado por `MappingSuggestionService`) sem duplicar o
contrato de `ILlmProvider` em dois formatos diferentes.
