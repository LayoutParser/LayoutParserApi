---
name: finetuning-smoke-test3-checkpointing-chunking
description: Smoke-test #3 (2026-08-29) mede gradient checkpointing (resolve OOM) e chunking (resolve perda de conteudo mas explode tempo) no fine-tuning Sysmiddle
metadata:
  type: project
---

Smoke-test #3 na VM Ubuntu (`elson@172.25.32.5`, chave `~/.ssh/lp_vm_ubuntu_ed25519`) mediu os
dois ajustes recomendados pelo smoke-test #2 pra resolver o OOM: gradient checkpointing e
chunking dos `.xsl` longos.

**Gradient checkpointing funciona: MAX_LEN=2048 roda sem OOM, RAM estável em ~11,56GB (de 15GB),
mas custa ~2,7x o tempo/passo** (~115s/passo vs. ~41-43s/passo em MAX_LEN=1024 sem checkpointing
no smoke-test #2). MAX_LEN=4096 continua não confiável mesmo com checkpointing (processo morreu
silenciosamente no meio do step 1, sem `dmesg`/`journalctl` disponíveis pra confirmar OOM com
certeza, mas padrão é consistente).

**Achado central, não óbvio:** com `padding="max_length"`, o tempo por passo é ~constante
independente do tamanho real do exemplo (todo passo processa os 2048 tokens completos). Isso
significa que **o custo total de treino escala com o número de exemplos, não com o volume de
conteúdo coberto** — e chunking (janela deslizante 2048/overlap 256, medido no dataset real de
259 pares) **multiplica o dataset por ~6,84x em média** (123/259 pares precisam de chunking,
1.772 exemplos no total, máximo 65 chunks num par de 153k caracteres). Resultado:
chunking com cobertura completa = **~56,6h só pra 1 época** — não cabe numa janela de fim de
semana (~48h). Truncar sem chunking (259 exemplos, MAX_LEN=2048) = ~8,3h/época, ~24,8h pra 3
épocas — cabe com folga, mas sacrifica conteúdo nos 47% de pares longos.

**Como aplicar:** antes de prometer "chunking resolve o problema", medir o multiplicador real do
dataset — não é garantido que chunking seja net-positive dentro de uma janela de tempo fixa,
porque ele resolve qualidade trocando por tempo, não é grátis. Veredito e opções detalhadas:
`docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md` (seção
"Smoke-test #3"). Relacionado: [[finetuning-smoke-test2-dado-real-oom]].
