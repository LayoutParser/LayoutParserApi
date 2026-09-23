#!/usr/bin/env bash
# Fase A (POC) do plano docs/architecture/plano-eval-benchmark-ia-2026-09-08.md.
#
# Roda `dotnet XslSynth.dll --mode=metrics-batch` uma vez por modelo (mesma dataset/--limit),
# reaproveitando 100% do binário/infra já publicados na VM (ver
# docs/architecture/plano-metricas-ia-servidor-producao.md §6) — nenhum código C# novo.
# Agrega o "Resumo do lote" (já impresso por MetricsBatchRunner.LogResumo) de cada modelo numa
# tabela markdown única em stdout, para não depender de grep manual em N blocos de log.
#
# NÃO fecha G2 (CPU/RAM) nem G3 (convergência do loop de reparo) — só a comparação lado-a-lado
# da similaridade estrutural/textual (mesma métrica que o metrics-batch já usa).
#
# Uso:
#   MODELS="qwen2.5-coder:7b qwen2.5-coder:14b layoutparser-sysmiddle-dsl:1.5b" \
#   DATASET=~/layoutparser-ai-metrics/dataset_pairs_filtered_v2.jsonl \
#   LIMIT=10 \
#   ./run-model-benchmark-comparison.sh
#
# LIMITAÇÃO HONESTA: não foi executado contra a VM real nesta sessão (sem acesso SSH nem Ollama
# local com os modelos grandes) — esqueleto revisável, valide antes de confiar no resultado.
set -euo pipefail

APP_DIR="${APP_DIR:-$HOME/layoutparser-ai-metrics}"
DATASET="${DATASET:-$APP_DIR/dataset_pairs_filtered_v2.jsonl}"
LIMIT="${LIMIT:-10}"
LOG_DIR="${LOG_DIR:-$APP_DIR/Logs/benchmark-comparison-$(date +%Y%m%d-%H%M%S)}"
MODELS="${MODELS:?defina MODELS=\"modelo1 modelo2 ...\"}"

mkdir -p "$LOG_DIR"

echo "# Benchmark comparativo de modelos — $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo
echo "Dataset: \`$DATASET\` · limit=$LIMIT · logs em \`$LOG_DIR\`"
echo
echo "| Modelo | Casos | Sucesso | Falhas | Tok/s médio | Duração média/caso (s) | TagOverlap médio | TextSim médio |"
echo "|---|---|---|---|---|---|---|---|"

for MODEL in $MODELS; do
  SAFE_NAME="$(echo "$MODEL" | tr '/:' '__')"
  OUT_FILE="$LOG_DIR/$SAFE_NAME.log"

  echo "── Rodando modelo: $MODEL ──" >&2
  if ! (cd "$APP_DIR" && dotnet XslSynth.dll --mode=metrics-batch \
        --dataset "$DATASET" --model "$MODEL" --limit "$LIMIT" \
        --log-dir "$LOG_DIR" --log-file "$SAFE_NAME.jsonl" > "$OUT_FILE" 2>&1); then
    echo "| $MODEL | — | — | — | — | — | — | (FALHOU — ver $OUT_FILE) |"
    continue
  fi

  # Extrai o bloco "Resumo agregado do lote" já impresso por MetricsBatchRunner.LogResumo.
  CASOS=$(grep -oP 'Casos\s+:\s+\K[0-9]+(?= \()' "$OUT_FILE" || echo "?")
  SUCESSO=$(grep -oP 'Casos\s+:\s+[0-9]+ \(\K[0-9]+(?= sucesso)' "$OUT_FILE" || echo "?")
  FALHA=$(grep -oP '[0-9]+ sucesso, \K[0-9]+(?= falha\))' "$OUT_FILE" || echo "?")
  TOKS=$(grep -oP 'Throughput médio\s+:\s+\K[0-9.]+' "$OUT_FILE" || echo "-")
  DUR=$(grep -oP 'Duração média/caso\s+:\s+\K[0-9.]+' "$OUT_FILE" || echo "-")
  TAG=$(grep -oP 'Tag overlap médio\s+:\s+\K[0-9.]+' "$OUT_FILE" || echo "-")
  TXT=$(grep -oP 'Text similarity média:\s+\K[0-9.]+' "$OUT_FILE" || echo "-")

  echo "| $MODEL | $CASOS | $SUCESSO | $FALHA | $TOKS | $DUR | $TAG | $TXT |"
done

echo
echo "Logs completos por modelo em: $LOG_DIR"
