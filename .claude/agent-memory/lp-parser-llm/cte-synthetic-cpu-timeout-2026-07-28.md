---
name: cte-synthetic-cpu-timeout-2026-07-28
description: qwen2.5-coder:7b em CPU não viabilizou geração sintética de pares CT-e por causa do tamanho do prompt (referências reais completas embutidas), não do modelo em si
metadata:
  type: project
---

Tentativa de gerar 6 pares sintéticos TCL→XSL de CT-e 4.00 (`gen_cte_synthetic.py`, script
em `.claude/tmp/dataset-finetuning/`) via `qwen2.5-coder:7b` local (Ollama, CPU-only,
sem GPU — ver [[sysmiddle-runtime-e-sintese]] no MEMORY.md do usuário sobre a topologia
Ubuntu/Ollama) resultou em **0 pares válidos, 6/6 descartados por timeout** (`timed out`
após 300s cada, ~30min de execução total no dia 2026-07-28).

**Causa raiz identificada (não é "modelo lento", é tamanho de prompt):** o script monta,
para cada uma das 6 tarefas, um prompt que embute **os arquivos TCL+XSL reais completos**
(referência 4.00 real + o par antigo a adaptar) como few-shot, com `num_ctx: 16384`. Um
teste de sanidade isolado (prompt trivial de 40 tokens) mostrou `prompt_eval_duration`
de 1.78s para 40 tokens de contexto — **~22 tokens/s de prefill em CPU**. Nesse ritmo,
um contexto de ~16k tokens levaria sozinho **~12 minutos só de prefill**, antes de emitir
qualquer token de saída — muito acima do timeout de 300s configurado no script.
(`load_duration` do teste foi 20.9s à parte — é carga do modelo em RAM, não inference;
não é o gargalo recorrente se o modelo já estiver quente.)

**Decisão de arquitetura (informação pra decisão futura, não implementada):** para
geração sintética *ancorada em documentos reais completos* (few-shot com arquivos grandes),
`qwen2.5-coder:7b` em CPU **não é viável** dentro de timeouts de poucos minutos — não é
questão de "esperar mais um pouco", é estrutural (throughput de prefill × tamanho do
prompt). Caminhos alternativos para retomar esta tarefa:

1. **Reduzir drasticamente o prompt** — usar excertos/resumo das referências reais em vez
   dos arquivos completos (ex.: só o cabeçalho + 1-2 blocos representativos), mantendo o
   "lastro em documento real" mas com contexto de poucas centenas de tokens.
2. **Aumentar o timeout** para algo compatível com o throughput medido (dezenas de minutos
   por chamada) — só se a geração puder rodar como job de fundo tolerante a isso, nunca
   síncrono a um request de usuário (ver [[lowcode-timeout-concurrency-sync-delivery]] —
   mesmo princípio de nunca bloquear resposta síncrona nisso já vale aqui).
3. **Aceitar bloqueio até GPU:** se nem com prompt reduzido for viável, essa etapa específica
   de dataset sintético fica pendente até existir GPU disponível — não insistir em CPU-only
   pra esse padrão de uso (few-shot com documentos grandes).

Recomendação imediata: se for retomar, tentar primeiro a opção 1 (prompt enxuto) com 3-4
tipos mais críticos antes de escalar timeout ou esperar por GPU.
