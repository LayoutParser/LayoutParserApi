#!/usr/bin/env bash
# Instala toolchain de fine-tuning (LoRA/QLoRA) na VM Ubuntu 172.25.32.5
# Rodar MANUALMENTE como usuario elson (sudo NOPASSWD ja configurado).
# CPU-only (4 vCPU / 15GB RAM / sem GPU) -> LoRA em modelo pequeno (1-1.5B),
# nao QLoRA (bitsandbytes exige CUDA). Treino sera lento; aceito pelo dono.
set -euo pipefail

echo "== apt: pip + build deps =="
sudo apt-get update -y
sudo apt-get install -y python3-pip python3-venv build-essential

echo "== recriando venv em ~/ft_venv =="
rm -rf ~/ft_venv
python3 -m venv ~/ft_venv
source ~/ft_venv/bin/activate
pip install --upgrade pip

echo "== pacotes de fine-tuning (CPU) =="
# torch CPU-only (sem CUDA, evita baixar ~2GB de wheel com suporte GPU inutil aqui)
pip install torch --index-url https://download.pytorch.org/whl/cpu
pip install transformers peft accelerate datasets trl sentencepiece protobuf

echo "== llama.cpp (para converter/quantizar o resultado pra servir via Ollama) =="
if [ ! -d ~/llama.cpp ]; then
  git clone --depth 1 https://github.com/ggerganov/llama.cpp ~/llama.cpp
fi
cd ~/llama.cpp
pip install -r requirements.txt
sudo apt-get install -y cmake
cmake -B build
cmake --build build --config Release -j "$(nproc)"

echo "== validacao =="
source ~/ft_venv/bin/activate
python3 -c "import torch, transformers, peft, datasets, trl; print('torch', torch.__version__, 'cuda?', torch.cuda.is_available())"

echo "OK - toolchain instalado. Axolotl/Unsloth NAO incluidos (Unsloth exige CUDA; Axolotl e opcional, avaliar depois com peft+trl puro)."
