---
name: finetuning-smoke-test2-dado-real-oom
description: Smoke-test #2 (2026-08-29) com dado real (155 pares tcl/xsl) confirma que RAM, não tempo, é o limite do treino LoRA na VM
metadata:
  type: project
---

Smoke-test #2 do plano de fine-tuning rodou com dado REAL (não sintético) pela primeira vez —
155 pares `tcl`/`xsl` reais localizados em `C:\inetpub\wwwroot\layoutparser\Examples\{tcl,xsl}\...`
na máquina de desenvolvimento, copiados pra VM (`elson@172.25.32.5:~/finetuning-dataset/`).
`.xsl` real varia 454–153.438 caracteres, média ~16,6k (confirma extrapolação anterior).

**Achado central: MAX_LEN=4096 → processo morto por OOM no primeiro passo de treino.**
MAX_LEN=1024 funciona mas já usa ~12Gi de 15Gi de RAM da VM, e leva ~41-43s/passo (vs. 10,7s/passo
no smoke-test #1 sintético de 256 tokens — custo ~linear nessa faixa, não quadrático ainda).

**Por quê importa:** a extrapolação anterior ("1,4h-2,3h pro treino completo") foi feita com
256 tokens/exemplo e não é válida pro dado real — a VM esgota RAM antes de esgotar tempo. Truncar
pra 1024 tokens evita o OOM mas descarta a maior parte do conteúdo real (mediana dos `.xsl` reais
é bem acima de 1024 tokens) — não é uma solução de qualidade pra Fase 1, só um dado de calibração.

**Como aplicar:** antes do treino completo de fim de semana, aplicar gradient checkpointing
(ataca a causa raiz — RAM, não tempo) e considerar chunking dos `.xsl` mais longos em vez de
truncar cru. Detalhe completo, números medidos passo a passo, e ordem de prioridade dos ajustes:
`docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md` (seção
"Smoke-test #2"). Relacionado: [[finetuning-poc-fase1-dataset]].
