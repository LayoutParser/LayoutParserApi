---
name: dev-ollama-vs-brnddappbld01-hardware
description: O Ollama respondendo em localhost:11434 no ambiente de dev NÃO é o servidor de produção BRNDDAPPBLD01 — hardware completamente diferente, não usar para benchmark real de throughput.
metadata:
  type: project
---

Ao preparar o item 3.6 do roadmap de IA (medir tok/s do Ollama para dimensionar
o modelo em produção), descobri que `http://localhost:11434` responde neste
ambiente de desenvolvimento (WSL) — mas é uma máquina Intel i5-1135G7 (Tiger
Lake, **com** AVX-512) com só `qwen2.5-coder:7b` (7.6B) baixado. O servidor de
produção real, `BRNDDAPPBLD01`, é um i7-4790 (Haswell 2014, 4c/8t, AVX2 **sem**
AVX-512), 32GB RAM, sem GPU — hardware bem mais fraco, e sem nenhum dos
candidatos 1-2B que o roadmap pede para medir (`qwen2.5:1.5b`, `llama3.2:1b`,
`gemma2:2b`, `smollm2:1.7b` ou equivalentes).

Validei a mecânica do script `Scripts/Benchmark-OllamaThroughput.ps1` contra
essa instância de dev (contrato JSON do `/api/generate` confere: `eval_count`,
`eval_duration` em nanossegundos, etc. - Ollama v0.31.2), mas o número que
saiu de lá (~3 tok/s num modelo 7.6B numa CPU diferente) não tem nenhum valor
preditivo para a produção - foi só smoke-test da ferramenta, não medição real.

**Why:** evita eu (ou outra sessão) confundir "testei localmente" com "medi a
produção" - são hardwares e modelos completamente diferentes, e apresentar o
número de dev como se fosse de produção seria literalmente "fingir que
mediu" (o que o dono do projeto pediu explicitamente para não fazer).

**How to apply:** para fechar de vez o item 3.6, `Scripts/Benchmark-OllamaThroughput.ps1`
precisa rodar FISICAMENTE a partir do `BRNDDAPPBLD01` (ou de algo com rede até
a porta 11434 dele), com os candidatos 1-2B já baixados lá (`ollama pull`).
Confirmar com @lp-devops se esse acesso existe, e se `BRNDDAPPBLD01` é a mesma
máquina do runner CI/espelho dev (suspeita não confirmada, levantada no
próprio roadmap - ver também a memória de infra do runner, se existir).
