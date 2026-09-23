---
name: eval-benchmark-ia-plano-2026-09-08
description: Design de eval-test/benchmark de modelos de IA para transformação; achado central é que boa parte já existe (metrics-batch) — não duplicar.
metadata:
  type: project
---

Pedido do dono (2026-09-08): suíte de eval-test/benchmark/análise estática da IA para fundamentar
decisão de escalamento (RAG-vs-fine-tuning, aumentar modelo base). Documento entregue:
`docs/architecture/plano-eval-benchmark-ia-2026-09-08.md`. Branch `feat/eval-benchmark-ia-plano`
(a partir de `develop`), commit local `33604c9`, **sem push** (regra do agente).

**Achado central:** `ai/XslSynth/Metrics/MetricsBatchRunner.cs` (`--mode=metrics-batch`) já é um
eval-test repetível contra qualquer modelo Ollama (`--model`), com métricas reais de
tokens/s e duração (nativas do Ollama) e qualidade (`OutputValidator`: TagOverlap Jaccard + LCS
text similarity). Roda semanalmente via cron na VM `172.25.32.31` desde 2026-07-30
(`docs/architecture/plano-metricas-ia-servidor-producao.md` §6). Itens 1 e 2 do pedido do dono
já existiam — o trabalho real era mapear os GAPS, não reconstruir do zero.

**Gaps reais identificados (G1-G6, detalhe no doc):**
- G1: nada compara N modelos na mesma rodada → POC implementado (`Scripts/vm/run-model-benchmark-comparison.sh`, roda metrics-batch por modelo e agrega tabela markdown). Não validado contra a VM real nesta sessão (sem acesso SSH).
- G2: CPU/RAM não medidos (só tokens/s e duração) — a inferência roda no processo `ollama`, não no `dotnet XslSynth.dll`; precisa `ps`/`/proc` polling do PID do ollama, não `/usr/bin/time` no processo errado.
- G3 (o mais importante para decisão real): `metrics-batch` mede similaridade tolerante do XSLT cru (1 chamada), não a taxa de convergência real (`diff==0` + XSD válido) do `RepairOrchestrator`/[[CanonicalDiffer]] que decide correção fiscal em produção. Rodar o `RepairOrchestrator` em lote por modelo é o benchmark que realmente decide "vale aumentar o modelo" — não existe hoje.
- G4: suíte sintética por categoria DSL (copy/concat/lookup/conditional) não existe — só dataset real (bom pra realismo, ruim pra diagnóstico fino de qual operação o modelo não sabe).
- G5/G6: sem lint estrutural de XSLT como gate rápido de CI, sem gate de regressão de qualidade em PRs que tocam `ai/**`.

**Por que isso importa para `@lp-qa` no futuro:** se pedirem para validar "o modelo X é melhor
que o Y", o critério certo NÃO é `TagOverlapRatio`/`TextSimilarityRatio` do metrics-batch atual —
é a taxa de convergência do `RepairOrchestrator` (Fase B do plano, ainda não implementada). Cobrar
isso antes de aceitar benchmark de modelo como evidência suficiente para decisão de escalamento.
