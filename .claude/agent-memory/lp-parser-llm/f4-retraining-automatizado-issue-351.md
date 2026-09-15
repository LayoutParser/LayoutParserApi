---
name: f4-retraining-automatizado-issue-351
description: F4.1/F4.2/F4.3 (retraining automatizado do modelo fine-tuned) implementado em C# na branch feat/retraining-automatizado-f4-351, commit b8aa8c3; lado da VM pendente (handoff @lp-devops).
metadata:
  type: project
---

Issue #351, ADR `docs/architecture/adr-backfill-catalogo-e-retraining-automatizado-2026-09-08.md`.
Branch `feat/retraining-automatizado-f4-351`, commit **b8aa8c3** (base = develop tip f56d3fb).
Build + 756 testes verdes; 22 testes novos.

**O que foi implementado (`Services/Transformation/Ai/Retraining/`):**
- **F4.1**: `RepairOrchestratorXslSynthesizerService.LogRuntimeMetrics` — Stopwatch em volta de
  `_orchestrator.RunAsync`, emite linha `Source=AiMetrics` ("RepairOrchestrator runtime concluido.
  ... DuracaoSegundos=.. Iteracoes=.. Convergiu=.. XsdValido=.."). Sempre, convergindo ou não.
- **F4.2**: `RetrainingOptions` (seção `XslSynth:Retraining`, `Enabled=false` default),
  `RetrainingState` (JSON durável via `FileRetrainingStateStore`, ao lado do JSONL de F3, NUNCA
  SQL), `RetrainingTriggerEvaluator` (puro: volume ≥ N OU ≥ 90d com ≥1 exemplo, nunca se
  TriggerPending), `RetrainingCoordinator` (Singleton: `RegisterCapturedExample` incrementa;
  `EvaluateAndMaybeTriggerAsync` detecta conclusão + escreve `retraining.trigger.json`),
  `RetrainingSchedulerBackgroundService` (a cada 6h). Hook: `TrainingDataCaptureService` ganhou
  param opcional `IRetrainingCoordinator?` e chama `RegisterCapturedExample()` após append OK.
- **F4.3**: `FileRetrainingLock` (lê `retraining.lock`, warning se > StaleLockAfterHours);
  `RepairOrchestratorXslSynthesizerService` checa `_retrainingLock?.IsHeld()` ANTES do Ollama →
  `Failed(...)` gracioso. `ModelMetricsComparer.Decide(current, candidate, tolerance)` — regressão
  em convergência/XSD/diff==0 → não promove.

**Pendente (lado VM `172.25.32.5`, handoff `@lp-devops`):**
`docs/architecture/handoff-f4-retraining-automatizado-lado-vm-351.md` — cron que observa o
marcador, cria/remove o lock, roda `train_lora.py`, valida pós-treino (`--mode=metrics-batch` +
regra do `ModelMetricsComparer`), promove (swap tag/symlink Ollama), e `Enabled=true` em prod.

**Gotcha desta sessão:** a working tree é COMPARTILHADA por múltiplos agentes concorrentes. Outro
agente fez `git checkout feat/xml-layout-sample-generator-356` no MEIO do meu trabalho e commitou.
Minhas mudanças (staged, sem conflito de arquivo) migraram no `git checkout` de volta pra minha
branch sem perda; commitei lá e restaurei a tree pra branch do outro agente. Sempre confirmar
`git branch --show-current` antes de commitar; usar `git commit -- <paths>` explícitos.
