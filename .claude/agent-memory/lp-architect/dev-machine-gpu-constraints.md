---
name: dev-machine-gpu-constraints
description: A máquina de dev (mesma do runner/WSL) só tem GPU integrada Intel Iris Xe, sem GPU discreta — tratar qualquer plano de LLM local como CPU-only até confirmar onde o Ollama vai rodar de fato
metadata:
  type: project
---

Confirmado pelo próprio usuário em 2026-07-21, direto na máquina: o ambiente WSL desta sessão só tem GPU
integrada Intel Iris Xe, **sem NVIDIA/AMD discreta** — `nvidia-smi` nem existe. `Get-CimInstance
Win32_VideoController` (lado Windows) reporta `AdapterRAM: 1073741824` (1GB), mas essa métrica de WMI é
conhecida por subestimar o teto real de iGPU Intel (memória compartilhada dinâmica, não pool fixo) — não
confiar no número exato, só na conclusão (sem GPU discreta). Presença de `libd3d12.so`/`libdxcore.so` em
`/usr/lib/wsl/lib/` confirma que o path de GPU aqui é DirectX/DirectML, não CUDA.

Pesquisa de 2026 (ver [[gemini-openai-decommission-decision]] pras fontes): suporte de iGPU Intel pra
inferência séria de LLM em 2026 segue fraco — NPU da Intel ajuda tarefa leve (cancelamento de ruído,
features do Copilot), não inferência de modelo. **Tratar como CPU-only pra fins de rodar LLM é a suposição
mais segura**, mesmo com a iGPU tecnicamente presente.

**Why importa:** invalidou uma recomendação de modelo que eu já tinha dado (`qwen3-coder:30b`/`devstral:24b`,
16-32GB de VRAM) — não cabe aqui. Isso é usado pra calibrar qualquer recomendação de modelo local (Ollama)
neste projeto daqui pra frente.

**Fechado em 2026-07-21 (rodada seguinte):** confirmado que o Ollama do diagnóstico roda no servidor de
produção `BRNDDAPPBLD01`, não nesta máquina de dev WSL — specs reais (Intel i7-4790 Haswell 2014, sem GPU
confirmada, 32GB RAM) documentadas em [[production-server-hardware]], que é agora a referência de hardware
pra qualquer recomendação de modelo/tamanho. Esta memória fica só como o achado original (máquina de dev é
iGPU-only) — não é mais o hardware relevante pra dimensionar o Ollama de produção.

Ponto 3 (treinar precisa de GPU, não só inferir) segue válido e agora reforçado: nem o servidor de produção
real tem GPU confirmada.

**How to apply:** nunca recomendar tamanho de modelo (Ollama ou qualquer treino local) sem antes confirmar
onde o processo vai rodar de fato. Na dúvida, recomendar a faixa pequena (2-4B params, CPU-friendly) como
piso seguro e sinalizar explicitamente que a recomendação sobe se houver GPU confirmada em outro lugar.
