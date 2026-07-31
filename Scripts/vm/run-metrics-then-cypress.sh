#!/usr/bin/env bash
# =============================================================================
#  run-metrics-then-cypress.sh — orquestrador Job 1 (metrics-batch) → Job 2 (Cypress/Pollux)
#
#  Ponto de entrada ÚNICO do cron da VM (172.25.32.31 / UBU220405RUN).
#  Substitui a chamada direta a run-metrics-batch.sh no crontab.
#
#  Dono: @lp-devops (Gage) · Contratos: docs/architecture/handoff-job2-cypress-batch.md
#  Runbook de provisionamento: docs/architecture/runbook-vm-cypress-provisionamento.md
#
#  ─────────────────────────────────────────────────────────────────────────────
#  POR QUE ESTE SCRIPT CHAMA O BINÁRIO DO JOB 1, E NÃO run-metrics-batch.sh
#  ─────────────────────────────────────────────────────────────────────────────
#  run-metrics-batch.sh existe SÓ na VM (nunca foi versionado). Não é possível
#  afirmar, de fora da VM, se ele bloqueia até o fim ou se faz `nohup … &`.
#  Em vez de compensar essa incerteza com polling, ela é ELIMINADA: chamamos
#  `dotnet XslSynth.dll --mode=metrics-batch` direto. Processo filho do shell
#  ⇒ bloqueio garantido pelo próprio `wait` implícito do bash; .NET não se
#  auto-daemoniza. Ganhos colaterais: controlamos os argumentos (--run-id
#  elimina a corrida "qual run é o mais recente") e o DOTNET_ROOT (o .NET está
#  em ~/dotnet, user-space, sem /etc/dotnet/install_location — o apphost
#  `XslSynth` pode não achar o runtime sob cron; `dotnet XslSynth.dll` acha).
#
#  Confirmado por leitura de código (2026-07-30): Program.cs faz
#  `return await RunMetricsBatchAsync()` e o MetricsBatchRunner percorre os casos
#  num `foreach` com `await`, com CloseAndFlush() no `finally` — não há Task.Run
#  nem fire-and-forget. O BINÁRIO não pode ser assíncrono; a única fonte possível
#  de assincronia era o `.sh`, que este wrapper deixou de usar.
#
#  O gate de manifesto (§2 do handoff: manifesto = sinal de "run completo") é
#  mantido como defesa em profundidade, com janela curta — NÃO como substituto
#  do bloqueio acima.
#
#  ─────────────────────────────────────────────────────────────────────────────
#  DEPENDÊNCIAS E ARMADILHAS DO CLI DO JOB 1
#  ─────────────────────────────────────────────────────────────────────────────
#  · `--run-id` / `--run-dir` (+ LP_METRICS_RUN_ID / LP_METRICS_RUN_DIR) foram
#    implementados pela @lp-parser-llm nesta sessão e ainda NÃO estão commitados
#    nem publicados na VM. Antes do primeiro deploy, confirme na VM:
#      grep -c 'run-dir' <fonte>  ·  ou rode --dry-run e veja o gate de manifesto.
#    Sem eles, o Job 1 roda em modo legado (só Serilog) e o wrapper sai com 2.
#  · `--limit 1` com ESPAÇO. `--limit=1` FALHA EM SILÊNCIO: o parser usa
#    Array.IndexOf(args, "--limit") (match exato de token), então com `=` o valor
#    vira null e o job roda os 54 pares (~3,6 h). A CLI é inconsistente de
#    propósito: `--mode=` usa `=`, `--limit` usa espaço.
#  · `--model` é DECORATIVO hoje — ver OLLAMA_MODEL mais abaixo.
#
#  ─────────────────────────────────────────────────────────────────────────────
#  CÓDIGOS DE SAÍDA
#  ─────────────────────────────────────────────────────────────────────────────
#    0  PASS   — Job 1 ok e veredito do Job 2 PASS (inclui "0 candidatos elegíveis")
#    1  FAIL   — veredito do Job 2 (ex.: candidato sem resposta do Pollux / infra_error)
#    2  FAIL   — manifesto ausente ou ilegível (Job 1 não entregou o contrato §2)
#    3  ABORT  — pré-condição de ambiente não satisfeita (nada foi executado)
#    4  SKIP   — já há uma execução em andamento (lock não adquirido)
#   10  FAIL   — Job 1 terminou com erro (código real registrado no log)
#   11  ABORT  — Job 1 ok, mas não há como executar o Job 2 (stack ausente)
#
#  NOTA: o exit code do `npx cypress run` é IGNORADO por contrato (§5.2 do
#  handoff) — ele sai ≠ 0 quando há candidato rejeitado, e rejeição é MEDIÇÃO,
#  não falha. Quem define o veredito é o verdict.js.
# =============================================================================

# SEM `set -e`: falhas são tratadas e registradas explicitamente. Um `set -e`
# aborta mudo no meio do encadeamento e perde o registro do que quebrou.
set -uo pipefail

# ─── Guarda de identidade: NUNCA rodar como root ─────────────────────────────
# Root só instala (apt/NodeSource, em /usr/*). Tudo que escreve sob /home/elson
# roda como elson. Motivo: o sink de arquivo do Serilog ENGOLE erro de escrita.
# Um único teste como root deixa Logs/layoutparserapi.log com dono root:root e a
# rodada seguinte roda as ~3,6h inteiras, queima a CPU e não grava UMA LINHA —
# sem erro visível. Reparo: chown -R elson:elson ~/layoutparser-ai-metrics ~/.cache/Cypress
if [ "$(id -u)" -eq 0 ]; then
  echo "ERRO: rode como elson, nao como root (use: su - elson -s /bin/bash -c '$0 ...')." >&2
  exit 3
fi

# ─── Configuração (tudo sobrescrevível por env var) ──────────────────────────
LP_HOME_JOB1="${LP_HOME_JOB1:-$HOME/layoutparser-ai-metrics}"
LP_HOME_JOB2="${LP_HOME_JOB2:-$HOME/layoutparser-cypress}"
METRICS_HOME="${METRICS_HOME:-$LP_HOME_JOB1}"

LP_DOTNET="${LP_DOTNET:-$HOME/dotnet/dotnet}"
LP_JOB1_DLL="${LP_JOB1_DLL:-$LP_HOME_JOB1/XslSynth.dll}"
LP_DATASET="${LP_DATASET:-$LP_HOME_JOB1/dataset_pairs_filtered_v2.jsonl}"
LP_MODEL="${LP_MODEL:-qwen2.5-coder:7b}"
LP_FEWSHOT_K="${LP_FEWSHOT_K:-3}"
LP_LIMIT="${LP_LIMIT:-}"                       # vazio = dataset completo
LP_OLLAMA_URL="${LP_OLLAMA_URL:-http://127.0.0.1:11434}"

LP_LOG_DIR="${LP_LOG_DIR:-$LP_HOME_JOB1/Logs}"  # Serilog do Job 1 (--log-dir)
LP_WRAPPER_LOG_DIR="${LP_WRAPPER_LOG_DIR:-$LP_LOG_DIR/wrapper}"
LP_LOG_RETENTION="${LP_LOG_RETENTION:-30}"      # nº de runs de log a manter

LP_SPEC="${LP_SPEC:-cypress/e2e/ia-candidates-batch.cy.js}"
LP_MANIFEST_GRACE_SECONDS="${LP_MANIFEST_GRACE_SECONDS:-120}"
LP_ALLOW_JOB1_ONLY="${LP_ALLOW_JOB1_ONLY:-0}"   # 1 = roda Job 1 mesmo sem stack do Job 2

LOCK_FILE="${LP_LOCK_FILE:-$METRICS_HOME/.run-metrics-then-cypress.lock}"

# PATH explícito: o cron NÃO carrega .bashrc/.profile. Tudo que o job precisa
# tem de estar aqui. LP_NODE_BIN cobre o cenário "Node por tarball em ~/node"
# (VM sem sudo — ver runbook §4.2).
export PATH="${LP_NODE_BIN:+$LP_NODE_BIN:}/usr/local/bin:/usr/bin:/bin:$HOME/dotnet:$HOME/.local/bin:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$LP_DOTNET")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# METRICS_HOME é lido pelo Job 1 como fallback para montar $METRICS_HOME/runs/<runId>.
export METRICS_HOME

# ⚠ OLLAMA_MODEL é o que REALMENTE decide o modelo da geração.
# `OllamaClient.Model` é get-only e inicializado de OLLAMA_MODEL no construtor
# (Synthesis/OllamaClient.cs:26); o `--model` do CLI só alimenta o LOG. Passar
# apenas --model faria o Serilog registrar um modelo e o Ollama gerar com outro
# — métrica inutilizável. Exportamos os dois com o mesmo valor até a @lp-parser-llm
# corrigir o bug (aí este export vira redundância inofensiva).
export OLLAMA_MODEL="${OLLAMA_MODEL:-$LP_MODEL}"

# CYPRESS_CACHE_FOLDER explícito e absoluto: o default (~/.cache/Cypress)
# resolve pelo HOME do processo. Se o cron rodar com HOME diferente do usuário
# que instalou, o binário "some" e o Cypress tenta baixar ~200 MB dentro do job.
export CYPRESS_CACHE_FOLDER="${CYPRESS_CACHE_FOLDER:-$HOME/.cache/Cypress}"

# ─── Flags de linha de comando ───────────────────────────────────────────────
DRY_RUN=0
RUN_ID=""
SELF_PATH="$(readlink -f "$0" 2>/dev/null || echo "$0")"

print_help() {
  cat <<EOF
Uso: $(basename "$0") [opções]

  --run-id <ID>       usa este runId (default: timestamp UTC compacto)
  --limit <N>         repassa --limit N ao Job 1 (teste rápido; default: dataset completo)
  --dry-run           valida pré-condições e imprime o plano, sem executar nada
  --print-cron        imprime a linha de crontab recomendada e sai
  -h | --help         esta ajuda

Variáveis de ambiente relevantes:
  LP_HOME_JOB1 (=$LP_HOME_JOB1)
  LP_HOME_JOB2 (=$LP_HOME_JOB2)
  LP_DOTNET (=$LP_DOTNET)          LP_JOB1_DLL (=$LP_JOB1_DLL)
  LP_MODEL (=$LP_MODEL)            LP_DATASET (=$LP_DATASET)
  CYPRESS_CACHE_FOLDER (=$CYPRESS_CACHE_FOLDER)
  LP_ALLOW_JOB1_ONLY (=$LP_ALLOW_JOB1_ONLY)   LP_NODE_BIN (=${LP_NODE_BIN:-<vazio>})
EOF
}

print_cron() {
  cat <<EOF
# Entrada ÚNICA no crontab do usuário elson (sábado 00:00). Substitui a linha
# que hoje chama run-metrics-batch.sh — NÃO manter as duas (rodariam em paralelo
# e disputariam CPU/Ollama; o lock impediria a 2ª, mas o agendamento duplo é ruído).
# O marcador no fim da linha é o mesmo que enable-metrics-job.sh/disable-metrics-job.sh
# procuram — mantenha-o, e atualize AQUELES scripts para apontar para este wrapper.
0 0 * * 6 $SELF_PATH >> $LP_WRAPPER_LOG_DIR/cron-wrapper.log 2>&1 # layoutparser-ai-metrics-batch

# Durante a transição (Job 2 ainda não provisionado), acrescente LP_ALLOW_JOB1_ONLY=1
# para não perder a série de métricas do Job 1:
# 0 0 * * 6 LP_ALLOW_JOB1_ONLY=1 $SELF_PATH >> $LP_WRAPPER_LOG_DIR/cron-wrapper.log 2>&1 # layoutparser-ai-metrics-batch
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --run-id)     RUN_ID="${2:-}"; shift 2 ;;
    --limit)      LP_LIMIT="${2:-}"; shift 2 ;;
    --dry-run)    DRY_RUN=1; shift ;;
    --print-cron) print_cron; exit 0 ;;
    -h|--help)    print_help; exit 0 ;;
    *) echo "Opção desconhecida: $1" >&2; print_help >&2; exit 3 ;;
  esac
done

RUN_ID="${RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)}"
RUNS_ROOT="$METRICS_HOME/runs"

# Contrato com o Job 2 (§4 do handoff): a spec descobre os candidatos lendo o
# manifesto deste diretório, em setupNodeEvents.
export LP_METRICS_RUN_DIR="$RUNS_ROOT/$RUN_ID"

# ─── Log com timestamp ───────────────────────────────────────────────────────
mkdir -p "$LP_WRAPPER_LOG_DIR" 2>/dev/null
WRAPPER_LOG="$LP_WRAPPER_LOG_DIR/run-$RUN_ID.log"
JOB1_LOG="$LP_WRAPPER_LOG_DIR/job1-$RUN_ID.log"
JOB2_LOG="$LP_WRAPPER_LOG_DIR/job2-$RUN_ID.log"

log() {
  printf '%s [wrapper] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" | tee -a "$WRAPPER_LOG"
}
tail_log() {  # despeja o fim do log de um job no log do wrapper (cron mail útil)
  local arquivo="$1" n="${2:-20}"
  [ -f "$arquivo" ] || return 0
  log "── últimas $n linhas de $(basename "$arquivo") ──"
  tail -n "$n" "$arquivo" | sed 's/^/    /' | tee -a "$WRAPPER_LOG"
}
finish() {  # código, mensagem
  log "FIM · runId=$RUN_ID exit=$1 · $2"
  log "logs: $WRAPPER_LOG"
  exit "$1"
}

# ─── Lock: não empilhar runs (um sábado atrasado sobre o outro) ──────────────
LOCK_MODE="none"
mkdir -p "$(dirname "$LOCK_FILE")" 2>/dev/null
if command -v flock >/dev/null 2>&1; then
  # `exec` é builtin especial: uma falha de redirecionamento derruba o shell sem
  # rodar o `||`. Por isso o teste de escrita vem ANTES, com mensagem legível.
  touch "$LOCK_FILE" 2>/dev/null || { echo "sem permissão de escrita em $LOCK_FILE" >&2; exit 3; }
  # fd 9 fica aberto pela vida do processo; o kernel libera o lock mesmo se o
  # script for morto (kill -9, reboot) — não existe lock órfão.
  exec 9>"$LOCK_FILE"
  if ! flock -n 9; then
    log "SKIP · já existe execução em andamento (lock $LOCK_FILE). Nada a fazer."
    exit 4
  fi
  LOCK_MODE="flock"
else
  # Fallback sem util-linux: lock por diretório + PID, com detecção de órfão.
  LOCK_DIR="$LOCK_FILE.d"
  if ! mkdir "$LOCK_DIR" 2>/dev/null; then
    ANTIGO="$(cat "$LOCK_DIR/pid" 2>/dev/null || echo "")"
    if [ -n "$ANTIGO" ] && kill -0 "$ANTIGO" 2>/dev/null; then
      log "SKIP · execução em andamento (pid $ANTIGO). Nada a fazer."
      exit 4
    fi
    log "AVISO · lock órfão (pid '$ANTIGO' morto) — reaproveitando."
    rm -rf "$LOCK_DIR"; mkdir "$LOCK_DIR" 2>/dev/null || { echo "lock indisponível" >&2; exit 3; }
  fi
  echo $$ > "$LOCK_DIR/pid"
  LOCK_MODE="mkdir"
  trap 'rm -rf "$LOCK_DIR"' EXIT INT TERM
fi

log "════════════════════════════════════════════════════════════════"
log "INÍCIO · runId=$RUN_ID · host=$(hostname) · user=$(id -un) · lock=$LOCK_MODE"
log "runDir : $LP_METRICS_RUN_DIR"
log "modelo : $LP_MODEL${LP_LIMIT:+  ·  LIMITE de teste: $LP_LIMIT caso(s)}"
log "cache  : CYPRESS_CACHE_FOLDER=$CYPRESS_CACHE_FOLDER"

# ─── Pré-condições (falhar aqui é barato; falhar depois de 4h, não) ─────────
ERROS=0
exigir() {  # teste, mensagem
  if ! eval "$1"; then log "PRÉ-CONDIÇÃO FALHOU · $2"; ERROS=$((ERROS + 1)); fi
}

exigir "[ -x \"$LP_DOTNET\" ]"     "dotnet não executável em $LP_DOTNET (ajuste LP_DOTNET)"
exigir "[ -f \"$LP_JOB1_DLL\" ]"   "binário do Job 1 ausente em $LP_JOB1_DLL (publique o XslSynth)"
exigir "[ -f \"$LP_DATASET\" ]"    "dataset ausente em $LP_DATASET"
exigir "mkdir -p \"$RUNS_ROOT\" 2>/dev/null && [ -w \"$RUNS_ROOT\" ]" "sem escrita em $RUNS_ROOT"
exigir "mkdir -p \"$LP_LOG_DIR\" 2>/dev/null && [ -w \"$LP_LOG_DIR\" ]" "sem escrita em $LP_LOG_DIR"

# Ollama: aviso, não erro. Um falso negativo aqui não pode cancelar a janela do
# fim de semana — o Job 1 já degrada caso a caso (1 caso falha → loga e segue).
if command -v curl >/dev/null 2>&1; then
  if curl -fsS --max-time 5 "$LP_OLLAMA_URL/api/tags" >/dev/null 2>&1; then
    log "ollama : OK ($LP_OLLAMA_URL)"
  else
    log "AVISO  · Ollama não respondeu em $LP_OLLAMA_URL/api/tags — o Job 1 pode falhar caso a caso."
  fi
fi

# Stack do Job 2 — verificada ANTES do Job 1 de propósito.
JOB2_MODO="nenhum"
if [ -x "$LP_HOME_JOB2/run-cypress-batch.sh" ]; then
  JOB2_MODO="script"
elif [ -f "$LP_HOME_JOB2/scripts/verdict.js" ]; then
  JOB2_MODO="inline"
fi
if [ "$JOB2_MODO" = "nenhum" ]; then
  if [ "$LP_ALLOW_JOB1_ONLY" = "1" ]; then
    log "AVISO  · stack do Job 2 ausente em $LP_HOME_JOB2 — rodando SÓ o Job 1 (LP_ALLOW_JOB1_ONLY=1)."
  else
    log "PRÉ-CONDIÇÃO FALHOU · Job 2 indisponível ($LP_HOME_JOB2: sem run-cypress-batch.sh nem scripts/verdict.js)."
    log "                      use LP_ALLOW_JOB1_ONLY=1 para rodar só o Job 1 durante a transição."
    ERROS=$((ERROS + 1))
  fi
else
  command -v node >/dev/null 2>&1 || { log "PRÉ-CONDIÇÃO FALHOU · 'node' não está no PATH do cron (PATH=$PATH)"; ERROS=$((ERROS + 1)); }
  command -v npx  >/dev/null 2>&1 || { log "PRÉ-CONDIÇÃO FALHOU · 'npx' não está no PATH do cron"; ERROS=$((ERROS + 1)); }
  [ -d "$CYPRESS_CACHE_FOLDER" ] || log "AVISO  · $CYPRESS_CACHE_FOLDER não existe — 'npx cypress verify' vai querer baixar o binário."
  log "job2   : modo=$JOB2_MODO ($LP_HOME_JOB2)"
fi

if [ "$ERROS" -gt 0 ]; then
  finish 3 "ABORT — $ERROS pré-condição(ões) não satisfeita(s). Nada foi executado."
fi

if [ "$DRY_RUN" = "1" ]; then
  log "DRY-RUN · pré-condições OK. Comando do Job 1 que seria executado:"
  log "  $LP_DOTNET $LP_JOB1_DLL --mode=metrics-batch --dataset $LP_DATASET --model $LP_MODEL \\"
  log "    --fewshot-k $LP_FEWSHOT_K --log-dir $LP_LOG_DIR --run-id $RUN_ID --run-root $RUNS_ROOT${LP_LIMIT:+ --limit $LP_LIMIT}"
  log "  depois: Job 2 (modo=$JOB2_MODO) com LP_METRICS_RUN_DIR=$LP_METRICS_RUN_DIR"
  finish 0 "DRY-RUN concluído (nada executado)."
fi

# ─── JOB 1 — geração de candidatos (bloqueante por construção) ───────────────
log "JOB 1 · iniciando metrics-batch · saída detalhada em $JOB1_LOG"
JOB1_INICIO=$(date +%s)

# --run-id/--run-dir são o contrato do Job 1 (Program.cs RunMetricsBatchAsync):
# argumento tem precedência sobre LP_METRICS_RUN_ID/LP_METRICS_RUN_DIR, que já
# exportamos acima. Passamos os dois — explícito no comando, redundante no env.
# Numa versão do Job 1 que ainda não leia essas flags, elas são ignoradas (o
# parser de args descarta flags desconhecidas) e o gate de manifesto abaixo
# acusa "Job 1 não entregou" — que é exatamente o sinal correto.
# NOTA: `--limit "$LP_LIMIT"` precisa ficar como DOIS tokens (espaço), nunca
# `--limit=N` — ver "armadilhas do CLI" no cabeçalho.
"$LP_DOTNET" "$LP_JOB1_DLL" \
  --mode=metrics-batch \
  --dataset   "$LP_DATASET" \
  --model     "$LP_MODEL" \
  --fewshot-k "$LP_FEWSHOT_K" \
  --log-dir   "$LP_LOG_DIR" \
  --run-id    "$RUN_ID" \
  --run-dir   "$LP_METRICS_RUN_DIR" \
  ${LP_LIMIT:+--limit "$LP_LIMIT"} \
  >>"$JOB1_LOG" 2>&1
JOB1_RC=$?
JOB1_DUR=$(( $(date +%s) - JOB1_INICIO ))

if [ "$JOB1_RC" -ne 0 ]; then
  log "JOB 1 · FALHOU (exit=$JOB1_RC) após ${JOB1_DUR}s — Job 2 NÃO roda (fail-fast)."
  tail_log "$JOB1_LOG" 30
  finish 10 "Job 1 falhou; nenhuma validação contra o Pollux foi tentada."
fi
log "JOB 1 · OK em ${JOB1_DUR}s ($(( JOB1_DUR / 60 )) min)."

# ─── Gate do manifesto (§2.3 do handoff: manifesto = run completo) ───────────
MANIFESTO="$LP_METRICS_RUN_DIR/manifest.json"
ESPERA=0
while [ ! -f "$MANIFESTO" ] && [ "$ESPERA" -lt "$LP_MANIFEST_GRACE_SECONDS" ]; do
  sleep 5; ESPERA=$((ESPERA + 5))
done
if [ ! -f "$MANIFESTO" ]; then
  log "MANIFESTO ausente após ${ESPERA}s em $MANIFESTO."
  log "  Causas prováveis: (a) o Job 1 ainda não implementa a escrita do run dir (contrato §2);"
  log "  (b) o run dir foi escrito em outro caminho. Job 2 NÃO roda com dado incompleto."
  finish 2 "Job 1 não entregou o manifesto — nada a validar."
fi
[ "$ESPERA" -gt 0 ] && log "MANIFESTO apareceu após ${ESPERA}s de espera."

# Manifesto ilegível/truncado também é FAIL(2) — melhor que rodar o Job 2 cego.
TOTAL_CAND="?"; TOTAL_ELEG="?"
if command -v node >/dev/null 2>&1; then
  RESUMO=$(node -e '
    const fs=require("fs");
    try {
      const m=JSON.parse(fs.readFileSync(process.argv[1],"utf8"));
      const c=Array.isArray(m.candidates)?m.candidates:[];
      process.stdout.write(c.length+" "+c.filter(x=>x&&x.eligibleForPollux).length);
    } catch (e) { process.stderr.write(String(e.message)); process.exit(1); }
  ' "$MANIFESTO" 2>>"$WRAPPER_LOG")
  if [ $? -ne 0 ]; then
    finish 2 "Manifesto ilegível ($MANIFESTO) — Job 2 não roda."
  fi
  TOTAL_CAND="${RESUMO%% *}"; TOTAL_ELEG="${RESUMO##* }"
fi
log "MANIFESTO OK · candidatos=$TOTAL_CAND elegíveis=$TOTAL_ELEG"

# Ponteiro 'latest' do contrato §2, escrito atomicamente por quem conhece o runId.
printf '%s\n' "$RUN_ID" > "$RUNS_ROOT/.latest.tmp" 2>/dev/null &&
  mv -f "$RUNS_ROOT/.latest.tmp" "$RUNS_ROOT/latest" 2>/dev/null &&
  log "runs/latest → $RUN_ID"

if [ "$JOB2_MODO" = "nenhum" ]; then
  finish 11 "Job 1 concluído; Job 2 não executado (stack ausente, LP_ALLOW_JOB1_ONLY=1)."
fi

# ─── JOB 2 — Cypress em modo batch contra o Pollux ───────────────────────────
log "JOB 2 · iniciando · LP_METRICS_RUN_DIR=$LP_METRICS_RUN_DIR · saída em $JOB2_LOG"
JOB2_INICIO=$(date +%s)

if [ "$JOB2_MODO" = "script" ]; then
  # Caminho normal: o script da @qa-cypress já encapsula
  # `cypress run || true` + `node scripts/verdict.js` (§5.2 do handoff).
  "$LP_HOME_JOB2/run-cypress-batch.sh" "$LP_METRICS_RUN_DIR" >>"$JOB2_LOG" 2>&1
  JOB2_RC=$?
else
  # Fallback enquanto run-cypress-batch.sh não existe: o MESMO contrato, inline.
  # O exit code do cypress é deliberadamente descartado — rejeição do Pollux é
  # medição, não falha do job. Quem decide o veredito é o verdict.js.
  ( cd "$LP_HOME_JOB2" && npx cypress run --spec "$LP_SPEC" ) >>"$JOB2_LOG" 2>&1
  CY_RC=$?
  log "JOB 2 · 'npx cypress run' saiu com $CY_RC — IGNORADO por contrato (§5.2)."
  ( cd "$LP_HOME_JOB2" && node scripts/verdict.js "$LP_METRICS_RUN_DIR" ) >>"$JOB2_LOG" 2>&1
  JOB2_RC=$?
fi
JOB2_DUR=$(( $(date +%s) - JOB2_INICIO ))

if [ "$JOB2_RC" -ne 0 ]; then
  log "JOB 2 · veredito FAIL (exit=$JOB2_RC) em ${JOB2_DUR}s."
  tail_log "$JOB2_LOG" 30
fi

SUMMARY="$LP_METRICS_RUN_DIR/cypress-summary.json"
if [ -f "$SUMMARY" ]; then
  log "resumo do Job 2 ($SUMMARY):"
  tr -d '\n' < "$SUMMARY" | cut -c1-500 | sed 's/^/    /' | tee -a "$WRAPPER_LOG"
else
  log "AVISO · $SUMMARY não foi escrito."
fi

# ─── Retenção de logs do wrapper ─────────────────────────────────────────────
ls -1t "$LP_WRAPPER_LOG_DIR"/run-*.log 2>/dev/null | tail -n +"$((LP_LOG_RETENTION + 1))" | xargs -r rm -f
ls -1t "$LP_WRAPPER_LOG_DIR"/job1-*.log 2>/dev/null | tail -n +"$((LP_LOG_RETENTION + 1))" | xargs -r rm -f
ls -1t "$LP_WRAPPER_LOG_DIR"/job2-*.log 2>/dev/null | tail -n +"$((LP_LOG_RETENTION + 1))" | xargs -r rm -f

if [ "$JOB2_RC" -eq 0 ]; then
  finish 0 "PASS — Job 1 (${JOB1_DUR}s) + Job 2 (${JOB2_DUR}s) concluídos."
fi
finish 1 "FAIL — veredito do Job 2 (exit=$JOB2_RC). Job 1 (${JOB1_DUR}s) foi bem-sucedido."
