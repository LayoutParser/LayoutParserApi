---
name: finetuning-vm-hardware-toolchain
description: Hardware real e toolchain de fine-tuning na VM Ubuntu 172.25.32.5 (dono reverteu decisao "sem fine-tuning")
metadata:
  type: project
---

Dono reverteu em 2026-09-02 a decisao anterior [[no-fine-tuning-ai-decision]] — agora quer LoRA/QLoRA
real neste dominio (parsing Sysmiddle -> TCL/XSLT/regras fiscais, NT2025.002 IBS/CBS), aceitando
treino de semanas/meses em hardware fraco.

**Hardware real da VM `172.25.32.5` (usuario `elson`, sudo NOPASSWD):**
- 4 vCPU, 15GB RAM (13GB disponivel), sem swap.
- **Sem GPU** (`nvidia-smi` nao existe) -> só LoRA CPU-only é viável, QLoRA/bitsandbytes exige CUDA.
- Disco: 59GB total, 32GB livres em `/`.
- Ollama 0.32.1 instalado, só `qwen2.5-coder:7b` baixado (4.7GB) — mesmo modelo do POC de 2026-07-28.
- Python 3.12.3 presente, **mas sem `pip3` no sistema** (nem apt nem venv com pacotes).
- `~/ft_venv` já existe (criado 2026-08-29) mas está **vazio** (32K, nenhum pacote) — scaffolding
  de sessão anterior, nunca populado.
- `git` 2.43.0 presente. Nenhum de axolotl/unsloth/llama.cpp/peft/transformers instalado.

**Gotcha de execução:** classificador de segurança do Claude Code bloqueia `sudo`/instalação de
pacote via SSH remoto mesmo com NOPASSWD configurado (já visto antes ao tentar instalar SQL Server
nesta mesma VM). Solução que funciona: gerar o script pronto e o dono roda manualmente — NÃO insistir
em contornar.

**Script de instalação gerado (pendente de execução manual do dono):**
`/tmp/claude-1000/.../scratchpad/install-finetuning-toolchain.sh` (efêmero — pedir para o agente
regenerar se o dono for rodar depois desta sessão). Conteúdo: apt (`python3-pip`, `python3-venv`,
`build-essential`, `cmake`), recria `~/ft_venv`, instala `torch` CPU-only + `transformers`/`peft`/
`accelerate`/`datasets`/`trl`/`sentencepiece`, clona e builda `llama.cpp` (para converter/quantizar
o LoRA resultante de volta a GGUF servível pelo Ollama). Decisão: **sem Unsloth** (exige CUDA) e
**sem Axolotl** por ora (avaliar depois com peft+trl puro primeiro, menos superfície pra debugar
em CPU-only).

**Dataset de treino (155 pares tcl->xsl) do POC de 2026-07-28 [[finetuning-poc-fase1-dataset]]
NÃO SOBREVIVEU** — vivia em `.claude/tmp/dataset-finetuning/` e a fonte em
`.claude/tmp/servidor/layoutparser/Examples/`, ambos dentro do scratch efêmero de sessão anterior,
confirmados ausentes em 2026-09-02. **Precisa re-extrair do zero** (repetir o script de correlação
`tcl/xsl` por `doctype/versao/stem`, ou localizar se `Examples/` ainda existe em algum outro caminho
do servidor via SSH — não verificado ainda).

**Redis local (`localhost:6379`) NÃO tem os mappers/DSL decriptados** — só cache de roteamento
(`Rules_CNPJ_*`, `mappers:search:all` = lista de destinos por CNPJ emit/dest, sem `DecryptedContent`).
Os 170 mapeadores reais com DSL estão no SQL Server (package `938f9978`, ver [[multi-client-mappers]]),
não no Redis. Extração de dataset direto do Redis, como pedido, **não é viável** — precisa ir na
fonte (SQL/`Examples/` no servidor de produção) via `@lp-backend-dev`/acesso ao banco.

Ver também [[xslsynth-trilha-a-workstream]] e [[dev-machine-gpu-constraints]] (constraint antiga da
máquina de DEV, distinta desta VM de treino).
