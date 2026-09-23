---
name: finetuning-smoke-test-vm-2026-08-29
description: Smoke-test real de fine-tuning LoRA na VM Ubuntu (172.25.32.5) — specs, bloqueio de sudo contornado, bitsandbytes não quantiza em CPU, tempo medido por passo
metadata:
  type: project
---

Executado em 2026-08-29 na VM `elson@172.25.32.5` (IP mudou de `.48` para `.5` no meio da sessão,
fixo agora). Detalhe completo em
`docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md` (seção
"Smoke-test executado em 2026-08-29").

**Specs reais:** Intel i7-4790 (4 núcleos físicos, sem HT, avx2), 15GB RAM, sem GPU (confirmado
`torch.cuda.is_available() == False`). Só `VMware SVGA II Adapter` (vídeo virtual, não compute).

**Gotcha de ambiente — sem `sudo` funcional (sem senha) e sem `python3-venv` pré-instalado:**
`apt install python3.12-venv` exige sudo com senha (não tenho); contornado sem tocar em rede/apt
via `python3 get-pip.py --user --break-system-packages` (Debian 12 "externally managed", PEP 668)
— instalou pip isolado em `~/.local/lib/python3.12/site-packages` sem root. `torch`/`transformers`/
`peft`/`accelerate`/`datasets`/`bitsandbytes` instalados assim com sucesso. Se a Fase 2 precisar de
outro pacote de sistema via `apt`, vai travar do mesmo jeito — avisar o dono com antecedência.

**bitsandbytes NÃO faz QLoRA de verdade em CPU-only:** instala e "carrega" um modelo com
`BitsAndBytesConfig(load_in_4bit=True)` sem erro, mas não há kernel 4-bit sem CUDA — é um falso
positivo silencioso, não lança exceção mas não quantiza nada de fato. Decisão técnica atualizada:
usar **LoRA em fp32 puro** (`peft.LoraConfig`, sem `BitsAndBytesConfig`) para a Fase 1, não QLoRA.

**Tempo medido (não estimado):** Qwen2.5-Coder-1.5B-Instruct + LoRA r=4 (q_proj/v_proj, 544k
params treináveis = 0,0353%), 10 pares sintéticos, seq_len=256, batch=1 → **107,3s para 1 época
(10,7s/passo)**. Carregamento do modelo do cache local: 1,6s (download inicial ~259s/~2.9GB).

**Extrapolação para 155 pares reais (seq_len 256):** ~28min/época, ~1,4-2,3h para 3-5 épocas —
cabe folgado na janela de fim de semana. **Risco não coberto:** `.xsl` reais médios ~16,6k chars
(muito além de 256 tokens) — atenção escala ~O(n²), tempo real por passo pode ser ordem de
grandeza maior. Próximo passo crítico antes do treino completo: rodar smoke-test #2 com 5-10 pares
*reais* no tamanho real (não sintéticos truncados) e medir de novo.

**Dataset real dos 155 pares não estava disponível nesta sessão** — foi gerado em sessão anterior
em `.claude/tmp/dataset-finetuning/` (fora de version control, ambiente reiniciado entre sessões).
Ver [[finetuning-poc-fase1-dataset]] para a lógica de extração (`Examples/tcl|xsl/<DocType>/
<Versao>/*`) — precisa ser reexecutada nesta VM ou os dados copiados pra lá antes do treino real.

**Erro de API do transformers (5.16.1):** `TrainingArguments(no_cuda=True)` foi removido — usar
`use_cpu=True` no lugar.
