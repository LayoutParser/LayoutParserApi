# Runbook — provisionamento do Job 2 (Cypress) na VM e ativação do wrapper

> **Executor:** `@lp-devops` (Gage) · **Alvo:** VM `172.25.32.31` (`UBU220405RUN`)
> **SO confirmado:** **Ubuntu 24.04.4 LTS (noble)** — o "2204" do hostname **não** é a versão do SO.
> **Acesso:** `ssh -i "$env:USERPROFILE\.ssh\layoutparser_automation" elson@172.25.32.31`
> **Contratos:** [`handoff-job2-cypress-batch.md`](handoff-job2-cypress-batch.md) ·
> **Plano:** [`plano-metricas-ia-servidor-producao.md`](plano-metricas-ia-servidor-producao.md) §7.5
> **Script:** [`Scripts/vm/run-metrics-then-cypress.sh`](../../Scripts/vm/run-metrics-then-cypress.sh)
> (versionado neste repo — atenção ao `S` maiúsculo: Windows não diferencia, Linux sim)

---

## 1. Regras de ownership — **leia antes de digitar qualquer comando**

Root instala; `elson` executa. Não é preciosismo: **o sink de arquivo do Serilog engole erro de escrita
por padrão.** Se um teste feito como root deixar `Logs/layoutparserapi.log` com dono `root:root`, a
rodada de sábado roda as ~3,6 h inteiras, queima a CPU do servidor e **não grava uma linha** — sem erro
visível, sem exceção, sem nada no console. O job "funciona" e a série de métricas fica vazia.

| # | Regra |
|---|---|
| **R1** | Como root: **apenas** `apt`/NodeSource, escrevendo só em `/usr/*`. |
| **R2** | Nada que escreva sob `/home/elson` roda como root: `npm ci`, `npx cypress install/verify/run`, o binário do Job 1, os wrappers, `ollama pull/rm`. |
| **R3** | Transição root→elson **sempre com shell de login**: `su - elson -s /bin/bash -c '…'` ou `sudo -u elson -H bash -lc '…'`. Sem o `-`/`-H`, `HOME` continua `/root` e o Cypress cacheia em `/root/.cache/Cypress`. |
| **R4** | `CYPRESS_CACHE_FOLDER=/home/elson/.cache/Cypress` absoluto em **três** lugares: `.npmrc` versionado do projeto, `export` no script, e neste runbook. |
| **R5** | Guarda no topo dos scripts (`run-metrics-batch.sh`, wrapper, `run-cypress-batch.sh`): `if [ "$(id -u)" -eq 0 ]; then echo "ERRO: rode como elson, nao como root"; exit 3; fi` — já implementada no wrapper. |
| **R6** | Caminhos **absolutos** no crontab e nos scripts, nunca `~`. |
| **R7** | A entrada do cron **precisa** redirecionar saída (`>> …/Logs/wrapper/cron-wrapper.log 2>&1`). Sem isso, o stdout vai para o mail local — que provavelmente não existe — e uma falha às 00:00 de sábado é invisível. |
| **R8** | Reparo após execução acidental como root: `chown -R elson:elson /home/elson/layoutparser-ai-metrics /home/elson/.cache/Cypress` |

### 1.1 Sobre "a VM tem sudo?" — os dois papéis

O teste `sudo -n true` que rodou na VM **foi tautológico**: executou como root, e `sudo` chamado pelo
próprio root sempre passa sem consultar o sudoers. Ele mediu a sessão root, não o usuário `elson`.

A leitura correta, compatível com todas as evidências:

- **Existe acesso root** para o operador humano ⇒ o `apt` das `.so` do Electron é possível. O bloqueio
  "não dá para instalar as dependências do Cypress" está **dissolvido**.
- **A identidade de automação é `elson`** — dona do crontab, da chave `layoutparser_automation` e do
  `~/dotnet` (foi por isso que o .NET foi para user-space). Ela provavelmente **segue sem privilégio**,
  e é ela quem executa o job.

As duas afirmações não se contradizem. Consequência prática: o **plano B do runner Node puro** (§8) deixa
de ser contingência forçada e passa a ser **simplificação opcional**.

---

## 2. Evidências — o que já está fechado e o que falta

### 2.1 Fechado

| Evidência | Resultado |
|---|---|
| Versão do SO | **Ubuntu 24.04.4 LTS (noble)** ⇒ usar **só** os nomes `t64` (§3) |
| Acesso root | Existe para o operador; `elson` (automação) presumidamente sem privilégio (§1.1) |
| Job 1 é síncrono? | **SIM, provado por leitura de código** — ver 2.2 |

### 2.2 Sincronicidade do Job 1 — resolvida por leitura de código, não por cronômetro

`ai/XslSynth/Program.cs:41` faz `return await RunMetricsBatchAsync()`, e `MetricsBatchRunner.RunAsync`
percorre os casos num `foreach` com `await`, com `CloseAndFlush()` no `finally`. **Não há `Task.Run` nem
fire-and-forget em lugar nenhum: o binário não pode ser assíncrono.** A única fonte possível de
assincronia seria o `run-metrics-batch.sh` — e o wrapper deixou de usá-lo (chama o `.dll` direto).

> ⚠️ **Não use** `time ~/layoutparser-ai-metrics/run-metrics-batch.sh --limit 1` como evidência: esse
> comando só vale se o script repassar `"$@"`, e a §6 do plano diz que ele roda o dataset **completo,
> sem `--limit`**. Se não repassar, você dispara os 54 pares (~3,6 h) achando que fez um smoke de 1 caso.
> `cat run-metrics-batch.sh` é evidência mais forte e instantânea.

### 2.3 Ainda falta coletar (bloco copiável)

```bash
ssh -i "$env:USERPROFILE\.ssh\layoutparser_automation" elson@172.25.32.31 'bash -s' <<'EOF'
echo "== identidade e disco =============================================="
id -un; hostname; df -h "$HOME" | tail -1     # binário do Cypress: ~500 MB descompactado
echo "== ferramentas presentes =========================================="
for c in node npx npm flock curl git tar xz Xvfb; do printf '%-8s %s\n' "$c" "$(command -v $c || echo AUSENTE)"; done
node --version 2>/dev/null
echo "== estado do Job 1 ================================================"
ls -la ~/layoutparser-ai-metrics/ | head -30
ls -ld ~/layoutparser-ai-metrics/Logs ~/layoutparser-ai-metrics/Logs/*.log 2>/dev/null   # R8: conferir dono
echo "== scripts NÃO versionados (a migrar — §9) ========================"
crontab -l
echo "---- run-metrics-batch.sh ----";  cat ~/layoutparser-ai-metrics/run-metrics-batch.sh
echo "---- enable-metrics-job.sh ----"; cat ~/layoutparser-ai-metrics/enable-metrics-job.sh
echo "---- disable-metrics-job.sh ---"; cat ~/layoutparser-ai-metrics/disable-metrics-job.sh
EOF
```

### 2.4 Chamada direta ao binário funciona? (**a evidência mais importante**)

O wrapper **não** usa o `run-metrics-batch.sh`. É obrigatório provar que a invocação equivalente funciona
sozinha, com o `DOTNET_ROOT` do user-space — **como `elson`, nunca como root** (R2):

```bash
cd /home/elson && DOTNET_ROOT=/home/elson/dotnet OLLAMA_MODEL=qwen2.5-coder:7b \
  /home/elson/dotnet/dotnet /home/elson/layoutparser-ai-metrics/XslSynth.dll \
  --mode=metrics-batch --limit 1 \
  --dataset /home/elson/layoutparser-ai-metrics/dataset_pairs_filtered_v2.jsonl \
  --log-dir /home/elson/layoutparser-ai-metrics/Logs
echo "exit=$?"
```

### 2.5 Duas armadilhas do CLI que já custaram tempo

| Armadilha | Detalhe |
|---|---|
| **`--limit=1` falha em silêncio** | O parser usa `Array.IndexOf(args, "--limit")` — match exato de token. Com `=`, o valor vira `null` e o job roda os **54 pares (~3,6 h)**. Use **`--limit 1`**, separado por espaço. A CLI é inconsistente de propósito: `--mode=` usa `=`, `--limit` usa espaço. |
| **`--model` é decorativo** | `OllamaClient.Model` é `get`-only e vem de `OLLAMA_MODEL` no construtor (`Synthesis/OllamaClient.cs:26`). O `--model` só alimenta o **log**. Passar só `--model` faz o Serilog registrar um modelo e o Ollama gerar com outro — métrica inutilizável. **Use `OLLAMA_MODEL`** (o wrapper já exporta os dois com o mesmo valor). Bug já reportado à `@lp-parser-llm`. |

### 2.6 Dependência aberta: `--run-id` / `--run-dir`

Foram implementados pela `@lp-parser-llm` **nesta sessão** (`Program.cs` + `Metrics/RunManifest.cs`),
mas ainda **não estão commitados nem publicados na VM**. O wrapper depende deles para o contrato §2 do
handoff. Sem eles, o Job 1 roda em modo legado (só Serilog), não escreve manifesto, e o wrapper sai com
código 2 — sinal correto, não silencioso. **Confirme antes do primeiro deploy real.**

---

## 3. Dependências de sistema (apt) — como **root** (R1)

**Ubuntu 24.04 (noble) — variantes `t64`. Não existe `libasound2` no noble**; um nome errado derruba o
`apt install` **inteiro**, não parcialmente.

```bash
apt-get update && apt-get install -y \
  libgtk2.0-0t64 libgtk-3-0t64 libgbm-dev libnotify-dev libnss3 libxss1 libasound2t64 libxtst6 xauth xvfb
```

Verificação opcional dos nomes antes de instalar (útil se o SO for atualizado no futuro):

```bash
apt-get update
PKGS=""
for p in libgtk2.0-0 libgtk-3-0 libgbm-dev libnotify-dev libnss3 libxss1 libasound2 libxtst6 xauth xvfb; do
  if   apt-cache show "${p}t64" >/dev/null 2>&1; then PKGS="$PKGS ${p}t64"
  elif apt-cache show "$p"      >/dev/null 2>&1; then PKGS="$PKGS $p"
  else echo "AVISO: nem '$p' nem '${p}t64' disponíveis"; fi
done
echo "Vai instalar:$PKGS"; apt-get install -y $PKGS
```

### 3.1 `xvfb` — decisão fechada

**Instalar o pacote SIM; usar `xvfb-run` NÃO.** "Headless" no Cypress é "sem janela", não "sem X server":
o Electron ainda precisa de um X. O Cypress detecta `DISPLAY` ausente e **sobe o Xvfb sozinho**, mas
exige o binário instalado (senão: `Your system is missing the dependency: Xvfb`). Envolver o comando em
`xvfb-run` por fora conflita com o Xvfb interno e mascara erros — especialmente sob cron, onde `DISPLAY`
nunca existe. O wrapper, por isso, **não** chama `xvfb-run`.

---

## 4. Node — versão e método

Cypress 15 **não** aceita Node 18. Confirme a faixa exigida pela versão fixada:

```bash
npm view cypress@15.19.0 engines     # esperado: node >=20.19 / >=22.12 / >=24
```

**Recomendação: Node 22 LTS via NodeSource** (como root, R1). Instala em `/usr/bin`, visível para
qualquer usuário e para o cron sem truque de `PATH` — que é exatamente o que se quer aqui:

```bash
curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
apt-get install -y nodejs
node --version   # v22.x
```

> **`nvm` está descartado.** Vive em `~/.nvm` e depende do `.bashrc`, que o cron **não** carrega — é a
> causa nº 1 de "roda no shell, quebra no cron" com Node.

### 4.1 Alternativa sem root (se a identidade de automação precisar do próprio Node)

```bash
cd /home/elson
curl -fsSL https://nodejs.org/dist/latest-v22.x/SHASUMS256.txt -o /tmp/node-sha.txt
TARBALL=$(awk '/linux-x64\.tar\.xz$/ {print $2}' /tmp/node-sha.txt)   # nome exato da 22.x atual
curl -fsSLO "https://nodejs.org/dist/latest-v22.x/$TARBALL"
grep " $TARBALL\$" /tmp/node-sha.txt | sha256sum -c -                  # integridade obrigatória
mkdir -p /home/elson/node && tar -xJf "$TARBALL" -C /home/elson/node --strip-components=1
/home/elson/node/bin/node --version
```

Depois informe o caminho ao wrapper (ele prepara o `PATH` do cron a partir daí):
`LP_NODE_BIN=/home/elson/node/bin`.

---

## 5. Repositório do Job 2 na VM — como **`elson`** (R2)

`LayoutParserCypress` **não tem remoto** (nem GitHub, nem push) — o deploy é local→VM.

```powershell
# Na workstation, a partir de C:\Users\elson.lopes\source\repos\LayoutParserCypress:
git bundle create ..\layoutparser-cypress.bundle --all
scp -i "$env:USERPROFILE\.ssh\layoutparser_automation" ..\layoutparser-cypress.bundle elson@172.25.32.31:/home/elson/
```

```bash
git clone /home/elson/layoutparser-cypress.bundle /home/elson/layoutparser-cypress
rm /home/elson/layoutparser-cypress.bundle
```

> O bundle é 1 arquivo, preserva histórico e dispensa remoto. Alternativa: `rsync -av --exclude
> node_modules --exclude .git`. **Nunca** copie `node_modules` — o binário vem do cache (§6).

### 5.1 `cypress.env.json` — credencial real, tratamento à parte

É **gitignored** e contém URL/credenciais reais do e-forms/Pollux. **Não vem no bundle** e **não pode**
ser commitado. Copie separadamente e restrinja a permissão:

```powershell
scp -i "$env:USERPROFILE\.ssh\layoutparser_automation" cypress.env.json elson@172.25.32.31:/home/elson/layoutparser-cypress/
```
```bash
chmod 600 /home/elson/layoutparser-cypress/cypress.env.json
```

---

## 6. Instalação do Cypress — como **`elson`** (R2/R3/R4)

**Pin:** `"cypress": "15.19.0"` exato (sem `^`) com `package-lock.json` commitado — trabalho da
`@qa-cypress` (Cass). Na VM, **sempre `npm ci`**, nunca `npm install`: `ci` respeita o lock byte-a-byte;
`install` pode subir minor silenciosamente numa máquina que ninguém observa.

```bash
su - elson -s /bin/bash -c '
  cd /home/elson/layoutparser-cypress
  export CYPRESS_CACHE_FOLDER=/home/elson/.cache/Cypress
  npm ci
'
```

Peça à Cass um `.npmrc` versionado no repo com `cypress_cache_folder=/home/elson/.cache/Cypress` (R4) —
redundância barata para quem rodar `npm ci` à mão sem exportar a var.

---

## 7. Validação, nesta ordem

```bash
su - elson -s /bin/bash -c '
  cd /home/elson/layoutparser-cypress
  export CYPRESS_CACHE_FOLDER=/home/elson/.cache/Cypress
  npx cypress verify   # valida binário + libs. É AQUI que falta de libgtk/libnss3/Xvfb aparece
  npx cypress info     # confirma browsers detectados
'

# Simulação do ambiente mínimo do cron — o teste que realmente importa.
# (Node por tarball? troque PATH por /home/elson/node/bin:/usr/bin:/bin)
su - elson -s /bin/bash -c 'env -i HOME=/home/elson PATH=/usr/bin:/bin:/usr/local/bin \
  CYPRESS_CACHE_FOLDER=/home/elson/.cache/Cypress \
  LP_METRICS_RUN_DIR=/home/elson/layoutparser-ai-metrics/runs/SMOKE \
  bash -c "cd /home/elson/layoutparser-cypress && npx cypress run --spec cypress/e2e/ia-candidates-batch.cy.js"'
```

> Passar no shell interativo e quebrar no cron por `PATH`/`HOME` é o modo de falha mais comum deste tipo
> de job. O `env -i` antecipa isso.

### 7.1 Contrato do exit code (validar com a Cass antes de ativar o cron)

Monte um `manifest.json` de teste em que **todos** os candidatos serão rejeitados pelo Pollux:

| Cenário | `npx cypress run` | `node scripts/verdict.js` | Esperado |
|---|---|---|---|
| Todos rejeitados | ≠ 0 (por design) | **0** | veredito PASS — rejeição é medição |
| Algum `infra_error` | qualquer | **1** | veredito FAIL |
| Manifesto ausente | — | **2** | veredito FAIL |

Se `verdict.js` devolver ≠ 0 no primeiro caso, o job vai "quebrar" todo fim de semana em que a IA gerar
XSLT ruim — exatamente o dado que queremos coletar.

---

## 8. Deploy do wrapper e troca do crontab (produção — confirmar antes)

```powershell
scp -i "$env:USERPROFILE\.ssh\layoutparser_automation" `
  Scripts\vm\run-metrics-then-cypress.sh elson@172.25.32.31:/home/elson/layoutparser-ai-metrics/
```
```bash
# `core.filemode=false` neste repo: o bit +x NÃO viaja no git/scp. Aplicar na mão:
chmod +x /home/elson/layoutparser-ai-metrics/run-metrics-then-cypress.sh

# Tudo abaixo como elson (o wrapper aborta com exit 3 se for chamado como root — R5):
/home/elson/layoutparser-ai-metrics/run-metrics-then-cypress.sh --dry-run     # valida sem executar
/home/elson/layoutparser-ai-metrics/run-metrics-then-cypress.sh --limit 1     # smoke real (~3-5 min)
/home/elson/layoutparser-ai-metrics/run-metrics-then-cypress.sh --print-cron  # linha pronta do crontab
```

Troca do agendamento — **entrada única**, substituindo a atual:

```bash
crontab -l > /home/elson/crontab.backup.$(date +%F)     # rollback
crontab -e                                               # trocar run-metrics-batch.sh pelo wrapper
crontab -l                                               # confirmar UMA linha com o marcador
```

> ⚠️ **`enable-metrics-job.sh` / `disable-metrics-job.sh` também precisam ser atualizados.** Eles
> reescrevem a entrada do crontab pelo marcador `# layoutparser-ai-metrics-batch`; se continuarem
> apontando para `run-metrics-batch.sh`, o próximo `enable` **desfaz** esta troca silenciosamente.
>
> ⚠️ Enquanto o Job 2 não estiver pronto, use a variante com `LP_ALLOW_JOB1_ONLY=1` impressa pelo
> `--print-cron` — assim a série de métricas do Job 1 não é perdida. Remova a var quando o Job 2 entrar.

### 8.1 Rollback

```bash
crontab /home/elson/crontab.backup.<data>
```
O wrapper não altera nada fora de `$METRICS_HOME/runs`, `$METRICS_HOME/Logs/wrapper` e do lockfile — não
há estado a desfazer além do crontab.

---

## 9. Simplificação opcional: runner Node puro (sem Electron)

Com root disponível (§1.1), isto **deixou de ser contingência** e virou escolha de arquitetura.

Os testes do repo são **100% `cy.request`** (SOAP/HTTP) — não há uma linha de interação de UI. A stack
gráfica inteira (Electron, GTK, Xvfb) é overhead. Um `node scripts/run-batch.js` que reuse a mesma lógica
SOAP e escreva **exatamente os mesmos artefatos** (`cypress-results.ndjson` + `cypress-summary.json`,
§3 do handoff) elimina `apt`, `xvfb` e Electron de vez.

- **Não implementar agora** — é simplificação, não pré-requisito.
- Mas **desenhar para ela**: a Cass mantém I/O e POST em módulos Node simples
  (`cypress/support/lib/*.js`) chamados por `cy.task`, não embutidos na spec. Assim vira um `main()`
  novo, não uma reescrita.
- **O wrapper já suporta:** basta `run-cypress-batch.sh` chamar o runner Node em vez do Cypress. O
  contrato do wrapper é o exit code do veredito — o que roda por baixo é indiferente.

### 9.1 Se um dia a identidade de automação precisar das `.so` sem root

`apt-get download <pkg>` (não exige root) + `dpkg -x <pkg>.deb ~/opt/deps` + `LD_LIBRARY_PATH`/`PATH`
apontando para lá funciona. O ponto frágil são as dependências transitivas — repita
`ldd "$(find "$CYPRESS_CACHE_FOLDER" -type f -name Cypress | head -1)" | grep 'not found'` e baixe o que
faltar. Se virar bola de neve, vá para o §9.

---

## 10. Dívida: scripts de produção não versionados

`run-metrics-batch.sh`, `enable-metrics-job.sh` e `disable-metrics-job.sh` existem **só na VM** — foi
exatamente isso que impediu responder "o Job 1 é síncrono?" sem SSH, e o que fez o comando de evidência
`time … --limit 1` ser inseguro (§2.2).

**Ação:** migrar os três para [`Scripts/vm/`](../../Scripts/vm/) (onde o wrapper já nasceu) e o deploy
passar a copiar de lá. Aria sugeriu `ai/XslSynth/scripts/`; mantive `Scripts/vm/` para concentrar os
scripts de operação da VM num só lugar e não misturá-los com o código do XslSynth. Coletar o conteúdo
atual pelo bloco da §2.3 antes de sobrescrever qualquer um deles.

---

## 11. Referência rápida — wrapper

| Env var | Default | Para quê |
|---|---|---|
| `LP_HOME_JOB1` | `$HOME/layoutparser-ai-metrics` | onde o Job 1 está publicado |
| `LP_HOME_JOB2` | `$HOME/layoutparser-cypress` | repo do Job 2 |
| `LP_DOTNET` | `$HOME/dotnet/dotnet` | .NET user-space |
| `LP_JOB1_DLL` | `$LP_HOME_JOB1/XslSynth.dll` | binário do Job 1 (chamado direto) |
| `LP_DATASET` | `$LP_HOME_JOB1/dataset_pairs_filtered_v2.jsonl` | dataset held-out |
| `LP_MODEL` | `qwen2.5-coder:7b` | exportado como `OLLAMA_MODEL` **e** passado em `--model` |
| `LP_LIMIT` | *(vazio)* | vira `--limit N` (dois tokens — §2.5) |
| `LP_NODE_BIN` | *(vazio)* | prepende ao `PATH` (Node por tarball) |
| `CYPRESS_CACHE_FOLDER` | `$HOME/.cache/Cypress` | **sempre** explícito sob cron (R4) |
| `LP_ALLOW_JOB1_ONLY` | `0` | `1` = roda Job 1 mesmo sem stack do Job 2 |
| `LP_MANIFEST_GRACE_SECONDS` | `120` | janela de tolerância do gate de manifesto |

| Exit | Significado |
|---|---|
| 0 | PASS (inclui "0 candidatos elegíveis" — estado válido) |
| 1 | FAIL do veredito do Job 2 (ex.: `infra_error`) |
| 2 | manifesto ausente/ilegível — Job 1 não entregou o contrato |
| 3 | pré-condição de ambiente, ou **execução como root** (R5) — nada foi executado |
| 4 | já havia execução em andamento (lock) — nada a fazer |
| 10 | Job 1 falhou (Job 2 não roda — fail-fast) |
| 11 | Job 1 ok, Job 2 indisponível (`LP_ALLOW_JOB1_ONLY=1`) |

Logs (todos sob `/home/elson/layoutparser-ai-metrics/Logs/wrapper/`): `run-<runId>.log` (wrapper, com
timestamp), `job1-<runId>.log`, `job2-<runId>.log`, `cron-wrapper.log`. Retenção: últimos 30 runs.
