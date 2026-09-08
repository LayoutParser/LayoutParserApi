---
name: adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08
description: ADR do dono (2026-09-08) — Sysmiddle sempre gabarito, IA sempre gera, corrige campo a campo 1:1, reaproveitando mapeamento existente; achado real é que 3/5 já existia desde a Issue #40.
metadata:
  type: project
---

Dono pediu (2026-09-07/08) fluxo: todo parse gera gabarito Sysmiddle automático → IA sempre
inicia a criação do TCL/XSL/XSLT (não mais opcional) → se já existe mapeamento pro layout,
reaproveita e corrige iterativamente (não recria do zero) → correção é 1:1 estrito campo a
campo (ex. `<nNF>1</nNF>` vs `<nNF>001</nNF>` tem que bater exato, não "semanticamente igual").

**Achado central da investigação:** 3 das 5 partes do pedido já existiam, desde a Issue #40 —
`TransformationExecutionController.TryEnqueueAiCandidate` já dispara sempre que há gabarito
Sysmiddle (comentário no próprio código confirma: "dono fechou que a IA 'sempre trabalha'"),
e `RepairOrchestrator`/`CanonicalDiffer` (`ai/XslSynth.Core`) já fazem loop gerar→diff
node-a-node estrito→corrigir até `diffs.Count==0 && xsd.IsValid`. Ver [[fine-tuning-nichado-ollama-2026-09-02]]
para o consumidor final do dataset (F4, fora de escopo deste ADR) e
[[reversao-pos-hoc-vs-cogeracao-2026-09-07]] para o subsistema correto (`MappingDraftRuleTranspiler`
tem só 5 operações — mas este ADR trata do pathway diferente, `RepairOrchestrator`/XSLT
sintetizado via Ollama contra gabarito Sysmiddle, não do transpiler determinístico de 5 ops).

**Os 2 gaps reais** (não implementados):
1. `RepairOrchestratorXslSynthesizerService.TryPersistXslt` grava o XSLT convergido em
   `{XslPath}/{mapperName}_{layoutName}.xsl` mas nunca lê de volta — cada novo documento do
   mesmo layout recomeça do `DeterministicXslTranspiler` do zero, não do XSLT já corrigido.
2. Nenhuma convergência em produção (`RunLoopAsync`/`RepairOrchestrator`) alimenta o dataset
   de treino — hoje só job batch offline (`MetricsBatchRunner`) produz `training-data/*.jsonl`.

**Por que importa pra #151:** o gap 2 é a peça que resolve o "corpus vazio" que bloqueava a
#151 (`tbMappingDraft`/`tbMappingRelease` com 0 linhas confirmado em 2026-09-07) — geração
automática volumosa passa a alimentar mappings TCL/XSLT publicados como efeito colateral do
parse normal, não dependendo de uso manual da feature Slice 5.

ADR completo: `docs/architecture/adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md`
(branch `docs/adr-convergencia-tcl-xslt-151`, commit local `94cea4f`, sem push/PR).
Comentado na issue #151. Fases propostas: F1 (seed de reaproveitamento, pequeno) + F2
(fallback anti-armadilha se seed for pior que baseline) + F3 (captura incremental de
dataset, pequeno, independente de F1/F2) + F4 (retraining, fora de escopo). Recomendação
pro `@lp-pm`: 2 issues novas (F1+F2 e F3), não uma só.

**Why:** decisão do dono, não escolha técnica livre — grounded 100% em código lido nesta
sessão (`TransformationExecutionController.cs`, `AiTransformationCandidateService.cs`,
`RepairOrchestratorXslSynthesizerService.cs`, `RepairOrchestrator.cs`), não suposição.

**How to apply:** antes de qualquer trabalho futuro em pathway IA/#151/#231, ler este ADR
primeiro — evita redesenhar o que já existe (erro que quase se repetiu aqui, já que o pedido
do dono soava como feature nova mas 60% já estava implementado).
