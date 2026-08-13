#!/bin/bash
# Desabilita o job semanal de metricas de IA (remove do cron do usuario elson).
set -eu
MARKER='# layoutparser-ai-metrics-batch'
{ crontab -l 2>/dev/null || true; } | grep -v "$MARKER" > /tmp/crontab.$$.tmp || true
crontab /tmp/crontab.$$.tmp
rm -f /tmp/crontab.$$.tmp
echo 'Cron job removido (se existia).'
if crontab -l 2>/dev/null | grep -q "$MARKER"; then
  echo 'AINDA PRESENTE!'
else
  echo 'Confirmado: nao ha mais entrada.'
fi
