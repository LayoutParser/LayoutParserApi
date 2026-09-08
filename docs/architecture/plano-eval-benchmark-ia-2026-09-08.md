# Plano — Eval-test, benchmark de modelos e análise estática da IA (2026-09-08)

> Pedido do dono: fundamentar com dado real a decisão de escalamento (inclusive a comparação
> RAG-vs-fine-tuning que `@lp-architect` está reavaliando em paralelo). Este documento é **fase de
> design** — só uma parte pequena (item 4) já foi implementada como POC.

## 0. O que já existe hoje (não duplicar)

Antes de desenhar qualquer coisa nova, o achado mais importante desta investigação: **os itens 1 e
2 do pedido (eval-test repetível + benchmark de múltiplos modelos) já existem, em produção,
rodando semanalmente.** O gap real é mais estreito do que parecia.

| Peça | Onde | O que faz |
|---|---|---|
| Eval-test repetível | `ai/XslSynth/Metrics/MetricsBatchRunner.cs` (`--mode=metrics-batch`) | Roda o dataset held-out (54 pares NFe/CTe/MDFe) contra **qualquer** modelo Ollama (`--model <nome>`), few-shot RAG (TF-IDF) → geração → validação → log estruturado `Source=AiMetrics`. Já é o "rodar contra qualquer modelo configurado", item 1 do pedido. |
| Métrica objetiva | `ai/XslSynth/Metrics/OutputValidator.cs` | `WellFormedXml`, `TagOverlapRatio` (Jaccard de nomes de elemento), `TextSimilarityRatio` (LCS, aproxima `difflib.SequenceMatcher.ratio`). `XsdValid` fica `null` fora do caso NFe emissão (sem oráculo XSD por operação). |
| Custo/latência real | `ai/XslSynth.Core/Synthesis/OllamaClient.cs` | tokens/s e duração vêm das métricas nativas do Ollama (`eval_count`/`eval_duration`), não estimadas. |
| Execução recorrente | `docs/architecture/plano-metricas-ia-servidor-producao.md` §6 | cron na VM `172.25.32.31`, sábado 00:00, já validado em produção desde 2026-07-30. |
| Comparação estrita 1:1 | `ai/XslSynth.Core/Core/CanonicalDiffer.cs` + `RepairOrchestrator.cs` | diff canônico node-a-node entre XML gerado e gabarito — usado no loop de reparo em produção, **não** no `metrics-batch` (que usa a métrica mais tolerante do `OutputValidator`, deliberadamente, porque mede o modelo cru sem o loop de reparo). |
| Suíte de transformação (pathway TCL/XSL clássico) | `Services/Testing/AutomatedTransformationTestService.cs` | Compara TXT→XML contra `expected_output.xml`, mas por **contagem de elementos + presença de elementos críticos** — não é diff estrito. Serve outro propósito (regressão do pathway legado), não é o alvo deste plano. |
| Análise estática de segurança | `security-code-scan-baseline.json` + `ci-dev.yml` | Já cobre SAST C# (não domínio de IA/XSLT). |

**Conclusão prática:** não vamos reconstruir "rodar eval contra N modelos" — já existe e roda. O
trabalho de design daqui pra frente é (a) fechar os gaps reais listados abaixo, (b) decidir onde
cada gap se encaixa no ciclo de vida (CI / nightly / manual).

## 1. Gaps reais (o que falta, priorizado)

### G1 — Nenhum mecanismo compara N modelos **na mesma rodada** e produz um veredito lado-a-lado
`--model` roda um modelo por invocação; hoje comparar 2 modelos exige rodar duas vezes e ler dois
blocos de log manualmente. Sem tabela agregada, "aumentar o modelo base" vira leitura manual de
log — exatamente o oposto de "decisão com dado real". **→ POC implementado nesta sessão (item 4).**

### G2 — Custo de recurso (CPU/RAM) não é medido, só tempo/tokens
O pedido pede "custo de recurso (CPU/RAM/tempo por inferência) — dado real, medido na mesma VM".
Hoje só duração e tokens/s existem. Modelo maior pode ganhar em qualidade e ainda ser inviável por
RAM (VM é CPU-only, sem GPU — RAM é o teto real de qual modelo cabe). Sem esse dado, a decisão de
"aumentar o modelo" fica coxa mesmo com o benchmark de qualidade pronto.

### G3 — O critério de "acertou" do benchmark não é o mesmo do sistema em produção
`OutputValidator` mede similaridade textual/estrutural tolerante (Jaccard de tags, LCS) — correto
para o cenário do `metrics-batch` (medir o modelo cru, sem loop de reparo). Mas o critério que
**decide correção fiscal** em produção é o diff estrito (`CanonicalDiffer`, `diff==0`) dentro do
`RepairOrchestrator`. Hoje não existe um número consolidado de "quantos casos convergem
(diff==0 + XSD válido) em N iterações, por modelo" — só "quão parecido ficou o XSLT cru". Para uma
decisão de escalamento, a métrica que importa de verdade é a de convergência do loop completo, não
a similaridade do primeiro palpite. Rodar o `RepairOrchestrator` (não só o `OllamaClient` cru) em
lote, por modelo, é o benchmark que faltava — mais caro (múltiplas chamadas por caso), por isso
não foi o que o `metrics-batch` original implementou.

### G4 — Casos sintéticos por categoria (copy/concat/lookup/conditional) não existem como suíte
O dataset de 54 pares é real e reflete a distribuição de produção (bom para benchmark realista),
mas não isola "o modelo entende `lookup`?" de "o modelo entende layout de CT-e inteiro?" — quando
um modelo falha, o dataset real não diz **qual operação** ele não sabe fazer. O teste manual de 4
prompts feito nesta sessão (copy/concat/conditional/lookup) tinha esse poder diagnóstico, só que
sem repetibilidade. Uma suíte pequena e sintética (10-20 casos, 1 por categoria de operação DSL:
copy, concat, conditional, lookup, date-format, numeric-format) complementa o dataset real —
não substitui, serve de "unit test" enquanto o dataset é o "integration test".

### G5 — Nenhum linting/validação estrutural do XSLT **antes** de aplicar
O `OutputValidator` só checa bem-formação depois de gerado. Falta uma checagem rápida e barata
(sem custo de LLM) reutilizável em CI: XSLT gerado é XML válido, tem `xsl:stylesheet` como raiz,
não referencia namespace incorreto — hoje isso só aparece indiretamente via `WellFormedXml=false`
no log, não como gate de CI que bloqueia PR.

### G6 — Sem gate de CI para regressão de qualidade de geração
Hoje nada impede um PR em `ai/XslSynth.Core` (transpilador, `DslRuleTranslator`, prompts) de piorar
silenciosamente a qualidade de geração — só o job semanal na VM detectaria, dias depois. Não existe
"rodar um subconjunto pequeno do eval-test a cada PR que toca `ai/**`".

## 2. Plano faseado

### Fase A (curto prazo, baixo custo) — feito nesta sessão como POC
Wrapper de comparação multi-modelo sobre a infra existente (fecha G1 parcialmente, sem tocar
C#). Ver §4.

### Fase B — Convergência do loop completo por modelo (fecha G3)
Novo modo `--mode=repair-batch` (ou flag no `metrics-batch` existente) que, por caso do dataset,
roda `RepairOrchestrator.RunAsync` (não só `OllamaClient.GenerateAsync`) e registra:
`Converged`, `Iterations`, `FinalDiffs.Count`, `FinalXsd.IsValid` — os campos que `SynthesisReport`
já expõe, hoje só usados no fluxo `--generate` interativo (1 caso), nunca em lote. É extensão
natural do `RepairOrchestrator` já existente, não coisa nova — o trabalho é o loop de lote +
log estruturado (mesmo padrão `Source=AiMetrics`, campo novo `Source=AiConvergence` ou reaproveitar
o mesmo schema com `Converged`/`Iterations` adicionados). Mais caro que o `metrics-batch` atual
(várias chamadas de LLM por caso, não 1) — candidato a rodar só nightly/semanal, com `--limit`
pequeno em CI.
Dono sugerido: `@lp-parser-llm`.

### Fase C — Captura de CPU/RAM real (fecha G2)
Na VM Linux, envolver a chamada ao `dotnet XslSynth.dll --mode=metrics-batch` com `/usr/bin/time -v`
(já disponível em Ubuntu sem instalar nada extra) capturando `Maximum resident set size` e
`Percent of CPU this job got` — **mas atenção**: a inferência roda no processo do **Ollama server**,
não no processo do XslSynth (o XslSynth só faz a chamada HTTP). Medir RSS do processo `ollama`
(via `ps`/`/proc/<pid>/status` no momento da chamada, ou `nvidia-smi` equivalente para CPU: nenhum,
é CPU-only) é o dado que realmente importa para "cabe no servidor". Proposta: script wrapper que,
durante a rodada, faz polling de `ps -o rss,%cpu -p $(pgrep ollama)` a cada N segundos e agrega
min/max/média — soma pouco código, sem mudar C#.
Dono sugerido: `@lp-devops` (acesso à VM) + `@lp-parser-llm` (integração do dado no relatório).

### Fase D — Suíte sintética por categoria de operação (fecha G4)
10-20 pares TCL→XSLT sintéticos mínimos, 1-3 por categoria (copy, concat, conditional, lookup,
date-format, numeric-format), versionados em `ai/XslSynth/training-data/eval-suite-sintetica/`
(separado do dataset real de treino/held-out — este é só para diagnóstico, nunca para treino).
Reaproveita o mesmo `--mode=metrics-batch --dataset <esse arquivo>` — não é modo novo, é dataset
novo. Menor prioridade que B/C porque o dataset real já dá sinal agregado; isto é para quando um
modelo específico precisar de diagnóstico fino ("por que caiu a nota, o que ele não sabe fazer").
Dono sugerido: `@lp-parser-llm` (conhece as categorias DSL) com `@lp-qa` revisando os gabaritos.

### Fase E — Lint estrutural + gate de CI (fecha G5, G6)
1. Função pequena e determinística (sem LLM) que valida: XML bem-formado, raiz
   `xsl:stylesheet`/`xsl:transform`, presença de `xsl:output`. Pode nascer como método estático em
   `OutputValidator` (reaproveitando o parse já feito) ou em `ai/XslSynth.Core/Core/` se for usada
   fora do contexto de métricas.
2. Step de CI (`.github/workflows/ci-dev.yml` ou workflow novo) que roda
   `--mode=metrics-batch --limit 5 --dry-run=false` contra um modelo pequeno/rápido em todo PR que
   toca `ai/**`, com um teto de qualidade mínimo (ex.: `TagOverlapRatio` médio não pode cair mais
   que X% vs. a última rodada nightly) — **não bloqueante no início** (`continue-on-error: true`),
   vira gate real só depois de ter baseline suficiente para não gerar falso-positivo.
Dono sugerido: `@lp-devops` (CI) com `@lp-qa` definindo o teto de qualidade.

## 3. Onde cada coisa roda (trade-offs)

| Peça | Onde | Por quê |
|---|---|---|
| Fase A (comparação multi-modelo) | Manual, sob demanda (script) | Decisão pontual e cara (múltiplos modelos grandes) — não é algo para rodar toda semana sem necessidade. |
| Fase B (convergência do loop) | Nightly/semanal na VM (estende o cron já existente) | Caro (múltiplas chamadas LLM por caso); frequência baixa é aceitável, é dado de tendência, não de PR. |
| Fase C (CPU/RAM) | Acoplado à Fase A e B (mesma execução, dado extra) | Não é uma execução própria, é instrumentação da que já existe. |
| Fase D (suíte sintética) | Manual/sob demanda + opcionalmente no gate de CI (Fase E), por ser barata (poucos casos) | Rápida o bastante para caber em CI sem estourar o orçamento de minutos. |
| Fase E (lint + gate CI) | CI, a cada PR em `ai/**` | É exatamente o caso de uso de CI: feedback rápido, caso pequeno, não pode esperar o nightly. |

## 4. POC implementado nesta sessão (Fase A, mínimo)

`Scripts/vm/run-model-benchmark-comparison.sh` — wrapper que roda `--mode=metrics-batch` para uma
lista de modelos (env `MODELS="qwen2.5-coder:7b qwen2.5-coder:14b layoutparser-sysmiddle-dsl:1.5b"`),
mesmo `--dataset`/`--limit`, e agrega os `Resumo do lote` (já logados pelo `MetricsBatchRunner`
existente, `LogResumo`) numa tabela markdown única em stdout — sem mudar nenhum código C#, só
orquestração de shell reaproveitando o binário publicado que já existe na VM (§6 do plano de
produção). Não implementa G2 (CPU/RAM) nem G3 (convergência) — só fecha a lacuna mecânica de "rodar
N modelos numa sessão e comparar sem grep manual".

**Limitação honesta:** não roda de verdade no ambiente de dev (sem Ollama local configurado com os
modelos grandes, sem acesso à VM nesta sessão) — não pôde ser validado empiricamente contra a VM
real. É esqueleto revisável, não uma execução comprovada. `@lp-devops`/dono devem rodar na VM antes
de confiar no output.

## 5. Recomendação de issue (não criada aqui — papel do `@lp-pm`)

Sugiro ao `@lp-pm` abrir 1 issue por fase (B a E), linkadas a este documento, com a Fase B como
prioridade mais alta (é o gap que mais distorce a decisão de "aumentar modelo": sem ela, decide-se
por similaridade textual do primeiro palpite, não pela taxa de convergência real do sistema em
produção).
