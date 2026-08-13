#!/bin/bash
# Job de métricas de geração de IA (XslSynth --mode=metrics-batch)
# Roda o dataset completo (54 pares) contra qwen2.5-coder:7b via Ollama local,
# gera log estruturado Serilog Source=AiMetrics para a série histórica.
set -euo pipefail

APP_DIR="$HOME/layoutparser-ai-metrics"
export DOTNET_ROOT="$HOME/dotnet"
export PATH="$PATH:$HOME/dotnet"

cd "$APP_DIR"
echo "[$(date -Iseconds)] Iniciando metrics-batch"
"$HOME/dotnet/dotnet" XslSynth.dll --mode=metrics-batch --dataset dataset/dataset_pairs_filtered_v2.jsonl --model qwen2.5-coder:7b --fewshot-k 3 --log-dir "$APP_DIR/logs" >> "$APP_DIR/logs/metrics-batch-run.log" 2>&1
echo "[$(date -Iseconds)] Finalizado metrics-batch (rc=$?)"
