---
name: finetuning-degeneracao-por-epoca-2026-08-30
description: Diagnóstico de degeneração por época no smoke-test de fine-tuning (par único NFe) — checkpoints nunca existiam, resultado inconsistente com smoke-test #4, mitigadores de geração não resolvem sozinhos.
metadata:
  type: project
---

Investigação de 2026-08-30 sobre por que o treino de 3 épocas (171 passos, 57 chunks, 1 par NFe
real, VM `elson@172.25.32.5`) degenerou em repetição alucinada.

**Achado 1 — checkpoints intermediários nunca existiram.** `smoke_train_single_pair.py` usa
`save_strategy="no"`; só o adapter final é salvo, sobrescrito a cada rodada. Sempre confirmar isso
lendo o script antes de assumir que dá pra "carregar o checkpoint da época X" — não tinha como.

**Achado 2 — VM tem teto duro de RAM: treino (~11,8GB pico) + `generate()` (~6,6GB) juntos
excedem os 15GB e o SO mata o processo de treino silenciosamente** (sem mensagem visível sem
`dmesg` root — o processo só some do `ps`). Aconteceu 2x nesta sessão. **Nunca rodar treino e
inferência concorrentes nesta VM.** Isso limitou a coleta ao checkpoint da época 1 (retreino
completo até 3 épocas ficou fora do orçamento de tempo).

**Achado 3 — resultado desta sessão é inconsistente com o smoke-test #4 documentado.** O
smoke-test #4 (mesma config: greedy, `MAX_LEN=2048`, 1 época, `max_new_tokens=1024`) reportava
convergência para XSLT válido com defaults semânticos corretos após ~1024 tokens de eco. A
reprodução desta sessão (checkpoint da época 1, mesmo par, script equivalente) produziu eco puro
sem nenhuma transição para XSLT (`xsl:` count = 0). Hipótese mais provável: sensibilidade a
não-determinismo em CPU (ordem de float ops/threading), não uma mudança real de protocolo — a
única diferença de script foi `save_strategy`. **Tratar "converge em N épocas" como resultado
frágil/não confiável neste protocolo até reproduzir de forma estável.**

**Achado 4 — `repetition_penalty=1.2` + `no_repeat_ngram_size=3` NÃO resolve a degeneração
sozinho.** Testado no adapter final de 3 épocas: elimina o padrão de repetição/eco degenerado, mas
o modelo migra para **alucinação de conteúdo fora de domínio** (JSON pseudo-estruturado
inventado), não para XSLT (`xsl:` continua em 0). Mitigador de geração troca o sintoma, não ataca
a causa (transição prompt→resposta mal aprendida).

**Recomendação registrada no plano:** não investir mais em variar número de épocas neste
protocolo; atacar a causa mais provável — truncamento do prompt a 1024 tokens de um `.tcl` real
com >10K tokens, que corta a âncora da transição prompt→resposta. Ver
`docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md`, seção
"Diagnóstico de degeneração por época — 2026-08-30", para o detalhe completo.

Relacionado: [[finetuning-small-model-poc]] (memória de usuário), [[finetuning-poc-fase1-dataset]].
