#!/bin/bash
# Habilita o job semanal de metricas de IA (sabado 00:00) via cron do usuario elson.
# Idempotente: remove entrada antiga com a mesma marca antes de adicionar.
set -eu
MARKER='# layoutparser-ai-metrics-batch'
JOB_LINE="0 0 * * 6 $HOME/layoutparser-ai-metrics/run-metrics-batch.sh $MARKER"

{ crontab -l 2>/dev/null || true; } | grep -v "$MARKER" > /tmp/crontab.$$.tmp || true
echo "$JOB_LINE" >> /tmp/crontab.$$.tmp
crontab /tmp/crontab.$$.tmp
rm -f /tmp/crontab.$$.tmp

echo 'Cron job habilitado:'
crontab -l | grep "$MARKER"
