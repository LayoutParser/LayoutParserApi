---
name: metrics-batch-mode-item1
description: Implementação do modo --mode=metrics-batch em ai/XslSynth (item 1 do plano de métricas de IA em produção) — como rodar, resultado do teste real limitado e decisões de design.
metadata:
  type: project
---

Item 1 de `docs/architecture/plano-metricas-ia-servidor-producao.md` implementado em
2026-07-29: `ai/XslSynth` ganhou o modo `--mode=metrics-batch`, elevando o spike Python
solto (ver [[rag-spike-cpu-throughput-2026-07-29]]) a um job real dentro do projeto,
pronto para rodar em lote no servidor (agendamento via Task Scheduler é item 2, escopo
do `@lp-devops`).

**Como rodar** (raiz do repo ou de dentro de `ai/XslSynth`):
```
dotnet run --project ai/XslSynth -- --mode=metrics-batch [--dataset <jsonl>] [--model <nome>] [--fewshot-k <n>] [--limit <n>] [--log-dir <pasta>]
```
Defaults: dataset = `.claude/tmp/dataset-finetuning/dataset_pairs_filtered_v2.jsonl` (54
pares), modelo = `qwen2.5-coder:7b` (ou env `OLLAMA_MODEL`), few-shot k=3, sem `--limit`
roda os 54 pares inteiros, log dir = `<raiz-do-repo>/Logs` quando encontrado (mesmo
arquivo `layoutparserapi.log` que a API/`UnifiedLogReaderService` já lê — sem dashboard
novo), senão cai para `Logs` local ao lado do exe.

**Arquivos criados/alterados:**
- `ai/XslSynth/Metrics/DatasetPair.cs` — modelo do par JSONL (`Load` degrada linha a linha, nunca derruba o carregamento).
- `ai/XslSynth/Metrics/DatasetFewShotIndex.cs` — TF-IDF/cosseno sobre o TCL de entrada, held-out por caso (nunca recupera a si mesmo). Vocabulário/IDF é GLOBAL sobre os 54 pares; só a recuperação exclui o próprio caso — mesmo princípio do spike Python anterior.
- `ai/XslSynth/Metrics/OutputValidator.cs` — bem-formado XML + TagOverlapRatio (Jaccard de nomes de elemento) + TextSimilarityRatio (LCS, aproxima `difflib.SequenceMatcher.ratio()`, truncado a 4000 chars por caso — custo O(n·m)). `XsdValido` fica sempre `null` — ver limitação abaixo.
- `ai/XslSynth/Metrics/MetricsBatchRunner.cs` — orquestra o loop inteiro + logging Serilog + resumo agregado. Cada caso roda dentro de try/catch próprio: 1 falha loga e o lote CONTINUA (não decidi usar Polly/retry — timeout de rede já é tratado dentro do `OllamaClient`, que devolve `Success=false` em vez de lançar).
- `ai/XslSynth/Synthesis/OllamaClient.cs` — estendido (não-destrutivo) com `GenerateWithMetricsAsync` que devolve `OllamaGenerationMetrics` (tokens/s e duração vindos dos campos nativos `eval_count`/`eval_duration`/`total_duration` do Ollama, não medição própria por Stopwatch — mais preciso). `GenerateAsync` antigo continua funcionando (chama o novo por baixo).
- `ai/XslSynth/Program.cs` — dispatch `--mode=metrics-batch` (ou `--metrics-batch`) antes dos outros modos.
- `ai/XslSynth/XslSynth.csproj` — pacotes `Serilog` 4.2.0 + `Serilog.Sinks.Console` 6.0.0 + `Serilog.Sinks.File` 6.0.0 (`Sinks.File` na mesma versão major da API).

**Decisões de design não especificadas no plano:**
1. **XSD real fora do loop** (documentado em `OutputValidator.cs`): o dataset mistura
   NFe/CTe/MDFe em operações variadas (emissão, cancelamento, consulta de status,
   inutilização…), cada uma com raiz/XSD SEFAZ próprio. Mapear "qual XSD valida qual
   caso" exigiria um catálogo caso-a-caso que não existe hoje — os XSDs plugados no
   `XsdValidator` cobrem só o leiaute NFe completo do fluxo `--generate`. `XsdValido`
   fica sempre `null` em vez de inventar uma validação com falso-negativo sistemático.
   A métrica que SEMPRE roda é a estrutural (tag overlap + text similarity).
2. **Recuperação por TF-IDF/cosseno (não Jaccard de traços)**: o `FewShotIndex`
   existente opera sobre `MapperRule` (regra DSL isolada, classificada por traço
   estrutural — if/else/&&). Este dataset é outra unidade: PAR inteiro schema-TCL→XSLT
   completo. Criei `DatasetFewShotIndex` novo (não reaproveitei `FewShotIndex`) porque a
   unidade de recuperação e a noção de similaridade são diferentes — reaproveitar
   forçaria um encaixe artificial.
3. **Ausência de fallback determinístico quando Ollama está fora do ar**: diferente do
   resto do projeto (que sempre degrada para um `MockFallback`), aqui a ausência do
   Ollama aborta o job inteiro com erro claro. Motivo: este modo EXISTE para medir o LLM
   real — um fallback mock produziria métricas sem sentido (throughput 0, texto
   determinístico) que poluiriam a série histórica sem nenhum valor.
4. **Log compartilhado**: resolvido subindo 2 níveis a partir de `.claude/tmp` até achar
   uma pasta `Logs` já existente na raiz do repo (mesma que a API usa por padrão,
   `Logging:File:Directory` = "Logs"). Testado e CONFIRMADO: as entradas caem no mesmo
   `Logs/layoutparserapi.log` que o `UnifiedLogReaderService` já lê.

**Resultado do teste real limitado** (2026-07-29, 2 casos de 54, `--fewshot-k 1`, ~6min):
ambos os casos (`CTe200_CancCTe_NeogridToSefaz`, `CTe200_consSitCTe_NeogridToSefaz`)
completaram com sucesso E2E (RAG → Ollama → validação → log). Throughput medido
**~3.3 tok/s** — MAIOR que o 1.3 tok/s do spike anterior (ver
[[rag-spike-cpu-throughput-2026-07-29]]); possível causa: prompt/caso diferente, cache de
modelo já quente no Ollama, ou variância normal — não dá para concluir tendência com N=2.
Um dos dois casos gerou XML malformado (`bem-formado=não`) mas ainda assim produziu
`tagOverlap=0.875`/`textSim=0.952` — a saída do LLM tinha conteúdo estruturalmente muito
próximo do gabarito com só um problema de fechamento/formação. Duração por caso ficou entre
2-4min — a rodada completa dos 54 pares em produção deve levar HORAS (compatível com o
racional do plano de rodar só em fins de semana no servidor).

**Rodada completa NÃO executada nesta tarefa** (por design — ficou para o servidor de
produção, conforme instrução explícita). O comando exato para lá está no runbook do
próprio plano (seção 6), só trocando `--limit` por nada (roda os 54).

**Why:** medir de verdade em vez de simular — a rodada completa precisa do servidor
ocioso por potencialmente horas, incompatível com uma sessão de trabalho interativa.

**How to apply:** ao revisar métricas reais dessa série (depois que o `@lp-devops`
agendar no servidor), filtrar `Source=AiMetrics` no log unificado; XsdValido sempre null
não é bug, é limitação documentada — não reportar isso como "0% de validação XSD" sem
essa ressalva.
