# Handoff — Job 2 (Cypress em modo batch) e o contrato Job 1 → Job 2

> **Autora:** `@lp-architect` (Aria) · **Data:** 2026-07-30
> **Destinatário primário:** `@qa-cypress` (Cass, repo `LayoutParserCypress`)
> **Destinatários secundários:** `@lp-backend-dev` (Dex, API) · `@lp-parser-llm` (Lia, `ai/XslSynth`) · `@lp-devops` (Gage, VM)
>
> Refina a §7.5 de [`plano-metricas-ia-servidor-producao.md`](plano-metricas-ia-servidor-producao.md),
> escrita por `@lp-devops`. Aquela seção listou 5 bloqueios; esta análise **encontrou mais 3, e dois
> deles invalidam o encadeamento como estava desenhado.** Leia a §1 antes de qualquer coisa.

---

## 1. Achados que redefinem o problema

Investiguei o que o Job 1 **de fato** produz (não o que o plano supõe). Quatro achados mudam o desenho:

### A1 — O Job 1 não persiste nada. Só loga.

`ai/XslSynth/Metrics/MetricsBatchRunner.cs` gera o XSLT candidato, valida em memória
(`OutputValidator`), grava uma linha Serilog e **descarta o candidato**. Não existe diretório de run,
manifesto, nem arquivo de saída. O verbo "os N candidatos gerados pelo Job 1" descreve algo que hoje
não existe em disco.

> **Consequência:** o Job 2 não tem o que consumir. Isso é trabalho de `@lp-parser-llm` (Lia) no
> `MetricsBatchRunner`, **não** da Cass — mas o contrato do artefato é definido aqui (§2).

### A2 — O artefato do Job 1 é um XSLT; o Pollux consome um XML de NF-e. Falta um elo.

O dataset é `input_map_tcl` (**schema** posicional) → `output_xslt`. O XSLT gerado espera como entrada
um documento `ROOT` (ex.: `<ROOT><chave><chNFe>…</chNFe></chave><Cabecalho><cUF>…`) e produz
`<NFe xmlns="http://www.portalfiscal.inf.br/nfe">`. Para chegar num XML submissível é preciso:

```
TXT de instância  +  schema TCL  →  ROOT.xml  +  XSLT gerado pela IA  →  NFe.xml  →  Pollux
                     └─ existe ─┘                  └─ existe ─┘            └── falta ──┘
```

O elo faltante é o **TXT de instância** e a aplicação do XSLT. Ambos são resolvíveis dentro do
XslSynth, que já tem `Core/RootTreeBuilder.cs` (TXT → ROOT) e `Core/XsltApplier.cs` (aplica XSLT).

**Verificado:** não existe endpoint na API que aplique um XSLT arbitrário fornecido pelo cliente —
`execute`, `execute-candidates` e `execute-lowcode` resolvem o XSLT pelo catálogo (`layoutName` → banco).
Criar um seria abrir execução de XSLT arbitrário via HTTP (XXE / `document()` / SSRF) numa API sem
autenticação — **rejeitado**. O Job 1 aplica o XSLT ele mesmo e grava o XML pronto.

### A3 — Só 4 dos 54 pares são elegíveis ao Pollux; na prática, hoje, 0 têm instância.

Quebra do dataset por operação (`dataset_pairs_filtered_v2.jsonl`, 54 pares):

| Grupo | Qtd | Elegível a `WSInserirDocumento`? |
|---|---|---|
| `NFe…EnvioNFe…` (raiz `<NFe>`) | **4** (2 mapas × 2 variantes de pasta) | **Sim** |
| `cancNFe` / `inutNFe` / `consSitNFe` / `consStatServ` / `evento` | 14 | Talvez — sem fixture SOAP nem oráculo validado |
| `…SefazTo…` (retornos, direção inversa) | ~24 | **Não** — SEFAZ→ERP, não faz sentido submeter |
| CT-e / MDF-e | 21 | Fora do escopo alpha do repo Cypress |

E o único TXT de instância que existe (`cypress/fixtures/txt-input/nfe-emissao-normal.mq_series.txt`,
formato `HEADER…` + registros de 600 chars, layout `LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe`)
**não casa** com o TCL dos pares do dataset, que usa `<LINE identifier="A" name="chave">`. São layouts
diferentes.

> **Decisão de escopo:** o Job 2 nasce com **N pequeno (1–4), não 54**. Isso é adequado, não um defeito —
> o valor é ter o oráculo final fechando o loop, não volume. Ver §7 (rota de crescimento).
>
> **Rejeitado:** sintetizar instâncias a partir do TCL. Dados sintéticos são rejeitados pela SEFAZ-fake
> por chave de acesso/DV/CNPJ/IE inválidos — o `cStat` mediria a qualidade do gerador de dados, não do
> XSLT. Ruído, não sinal.

### A4 — O painel do Gap 3 está desconectado do Job 1 (bug de integração pré-existente)

- API lê: `Logging:File:Directory` = `C:\inetpub\wwwroot\layoutparser\api\logs\layoutparserapi.log` (**Windows**).
- Job 1 escreve: `~/layoutparser-ai-metrics/Logs/layoutparserapi.log` (**VM Linux 172.25.32.31**).

São arquivos distintos, em máquinas distintas. Portanto **hoje**:
`GET /api/ai-metrics/generations` retorna vazio, e o `POST /api/ai-metrics/cypress-result` grava a linha
`Cypress validado.` no log da API, mas o merge por `Layout` do `AiMetricsReaderService` não encontra
geração alguma para casar. O endpoint funciona; a integração não fecha.

**Opções avaliadas:**

| Opção | Custo | Veredito |
|---|---|---|
| **A. Endpoint de ingestão de gerações** na API (simétrico ao `cypress-result` já existente); o wrapper faz POST do lote ao final | ~1 controller action + model; reusa padrão já aprovado | **Recomendada** |
| B. Montar (CIFS/SMB) o diretório de logs do Windows na VM e apontar `--log-dir` pra lá | Zero código | **Rejeitada** — Serilog `shared:true` sobre SMB é receita de lock/corrupção; acopla VM ao Windows por FS de rede |
| C. Copiar/append o log da VM pro Windows no fim do job | Script | **Rejeitada** — parse/dedup frágil, duplica em rerun |

> **Ação:** `@lp-backend-dev` (Dex) — `POST /api/ai-metrics/generations/ingest`, aceitando um array das
> mesmas chaves da linha `Geracao concluida.`. Idempotência pelo mesmo mecanismo de merge lógico já usado.
> **Não bloqueia a Cass** — o contrato dela (§3) é gravar em disco + POST best-effort; ele passa a fazer
> efeito no painel quando A4 for resolvido.

---

## 2. Contrato de ENTRADA (Job 1 → Job 2)

Diretório de run versionado, com manifesto escrito por último (commit atômico).

```
$METRICS_HOME/runs/<runId>/            # runId = timestamp UTC compacto, ex. 20260801T000000Z
├── manifest.json                       # índice — escrito POR ÚLTIMO
└── candidates/
    └── <candidateId>.xml               # XML de NF-e PRONTO para submissão (não o XSLT)
$METRICS_HOME/runs/latest               # arquivo texto de 1 linha com o runId mais recente
```

`manifest.json`:

```json
{
  "schemaVersion": 1,
  "runId": "20260801T000000Z",
  "startedAt": "2026-08-01T00:00:03Z",
  "finishedAt": "2026-08-01T03:41:12Z",
  "model": "qwen2.5-coder:7b",
  "totalCases": 54,
  "candidates": [
    {
      "candidateId": "NFe_4.00_NFe009_4.00_EnvioNFe_NeoGridToSefaz",
      "layout": "NFe\\4.00\\NFe009_4.00_EnvioNFe_NeoGridToSefaz",
      "docType": "NFe",
      "operation": "envio",
      "eligibleForPollux": true,
      "xmlPath": "candidates/NFe_4.00_NFe009_4.00_EnvioNFe_NeoGridToSefaz.xml",
      "sourceFixture": "nfe-emissao-normal.mq_series.txt",
      "xsdValid": null,
      "notes": null
    }
  ]
}
```

**Regras inegociáveis do contrato:**

1. **`layout` é a chave de junção com a API e deve ser byte-a-byte igual** ao campo `Layout` da linha
   Serilog `Geracao concluida.` — que é o `DatasetPair.Id`, **com barras invertidas**
   (`NFe\4.00\NFe009_…`). No JSON isso é `\\`. Se divergir (normalizar pra `/`, trocar caixa, cortar
   prefixo), o merge do `AiMetricsReaderService` silenciosamente não casa e o painel fica vazio **sem
   erro nenhum**. É o modo de falha mais provável desta integração.
2. **`candidateId`** é o `layout` com separadores trocados por `_` — seguro para nome de arquivo.
3. **Escrita atômica:** grave os XMLs primeiro; grave `manifest.json` como `manifest.json.tmp` e faça
   `rename()` por último. A existência do manifesto é o sinal "run completo". Job 1 morto no meio ⇒
   Job 2 não encontra manifesto ⇒ não roda ⇒ nenhum dado corrompido entra no painel.
4. **`eligibleForPollux`** já vem decidido pelo Job 1 (só `docType=NFe` + `operation=envio` + XML
   bem-formado hoje). O Job 2 **não reimplementa** essa regra — só filtra por ela. Assim, ampliar o
   escopo (cancelamento, CT-e) não exige mexer na spec.
5. **`candidates` pode ser `[]`.** É estado válido (rodada onde nada passou na validação). Não é erro.

**Localização:** `METRICS_HOME` = `/home/elson/layoutparser-ai-metrics` na VM. Nunca hardcode; a spec
recebe o caminho por env var (§4).

---

## 3. Contrato de SAÍDA (Job 2 → API/painel) — a parte que faltava

Duas metades, nesta ordem de prioridade. **Disco é a fonte da verdade; a API é best-effort.**

### 3.1 Artefato em disco (obrigatório, incremental)

```
$METRICS_HOME/runs/<runId>/
├── cypress-results.ndjson     # 1 linha por candidato, APPEND assim que o candidato termina
└── cypress-summary.json       # escrito no final (after:run), agrega o NDJSON
```

`cypress-results.ndjson`, uma linha por candidato:

```json
{"schemaVersion":1,"runId":"20260801T000000Z","candidateId":"NFe_4.00_NFe009_4.00_EnvioNFe_NeoGridToSefaz","layout":"NFe\\4.00\\NFe009_4.00_EnvioNFe_NeoGridToSefaz","outcome":"accepted","cypressValidado":true,"cStatPollux":"100","protocolo":"…","mensagemGeral":"Processo de consulta realizado com sucesso","mensagemItem":"Processo realizado com sucesso","observacao":null,"durationMs":8213,"posted":true,"timestamp":"2026-08-01T03:44:10Z"}
```

`outcome` é um enum de **três** valores — e essa distinção é o coração do desenho:

| `outcome` | Significado | `cypressValidado` | Conta como falha do Job 2? |
|---|---|---|---|
| `accepted` | Pollux autorizou (`cStat=100` ou mensagens de sucesso) | `true` | Não |
| `rejected` | Pollux respondeu e **recusou** (cStat ≠ 100, sem protocolo, erro de validação fiscal) | `false` | **Não — isto é o dado que queremos** |
| `infra_error` | Não houve veredito: Pollux inacessível, timeout, HTTP ≠ 200, resposta ilegível | `null` | **Sim** |

> Confundir `rejected` com `infra_error` é o erro clássico aqui. Rejeição é a **medição**; ausência de
> resposta é a **falha**. Um XSLT ruim gerando XML recusado é exatamente o sinal que o painel precisa —
> não pode ser tratado como quebra do job.

`cypress-summary.json`:

```json
{"schemaVersion":1,"runId":"…","startedAt":"…","finishedAt":"…",
 "total":4,"accepted":1,"rejected":2,"infraError":1,"posted":3,"postFailed":1,
 "verdict":"FAIL","verdictReason":"1 candidato(s) sem veredito do Pollux (infra_error)"}
```

### 3.2 POST best-effort para a API (não bloqueante)

Para cada candidato com `outcome ∈ {accepted, rejected}`:

```
POST {layoutParserApiUrl}/api/ai-metrics/cypress-result
Content-Type: application/json

{ "layout": "NFe\\4.00\\NFe009_4.00_EnvioNFe_NeoGridToSefaz",
  "cypressValidado": true,
  "cStatPollux": "100",
  "observacao": "runId=20260801T000000Z protocolo=…" }
```

Contrato já implementado e estável — `Controllers/AiMetricsController.cs` (`PostCypressResult`) +
`Models/Logging/AiMetricsModels.cs` (`AiMetricsCypressResultRequest`). Só 4 campos; `layout` é obrigatório.

**Regras:**
- Falha de POST (API parada no fim de semana, rede indisponível) **nunca** falha o candidato nem o Job 2.
  Marque `"posted": false` no NDJSON e siga.
- `infra_error` **não** é postado (postar `cypressValidado:null` polui o painel com "pendente" indistinguível
  de "nunca rodou").
- O endpoint é idempotente por construção (merge lógico pela entrada mais recente por `Layout`) — reenviar
  é seguro. Isso habilita um **replay**: `npm run replay:results -- <runId>` relê o NDJSON e reenvia só os
  `posted:false`. Especifique-o; é 20 linhas e evita perder uma rodada de 4h por causa de rede.

---

## 4. Como a spec descobre e itera sobre os candidatos

**Recomendação: ler o manifesto em `setupNodeEvents` e injetar a lista em `config.env`.**

```js
// cypress.config.js — setupNodeEvents
const runDir = process.env.LP_METRICS_RUN_DIR;      // caminho absoluto do run
// lê manifest.json de forma síncrona AQUI (processo Node, antes de a spec carregar)
config.env.lpCandidates = lerManifestoOuListaVazia(runDir);
config.env.lpRunDir = runDir;
return config;
```

```js
// spec — geração SÍNCRONA dos it(), sem I/O
const candidatos = (Cypress.env("lpCandidates") || []).filter(c => c.eligibleForPollux);
describe("Job 2 — candidatos IA vs Pollux", () => {
  if (candidatos.length === 0) { it("nenhum candidato elegível nesta rodada", function () { this.skip(); }); return; }
  candidatos.forEach((c) => { it(`${c.candidateId}`, () => { /* … */ }); });
});
```

**Por que esta e não as alternativas:**

| Abordagem | Veredito |
|---|---|
| `cy.task` lendo o diretório dentro de `before()` | **Não funciona.** Mocha registra os `it()` no *load* da spec; um `cy.task` async em `before()` roda depois — não dá para criar casos dinamicamente. É a parede em que todo mundo bate primeiro. |
| `fs.readdirSync` direto na spec | **Não funciona.** A spec roda no browser; não há `fs`. |
| `setupNodeEvents` + `config.env` (**recomendada**) | Roda em Node, síncrono, **antes** do load da spec. Um `it()` por candidato ⇒ isolamento nativo do Mocha e relatório legível. |
| Loop de shell com `cypress run --spec` por candidato | Paga ~5–10 s de boot × N, fragmenta o relatório em N execuções. Só valeria se cada candidato precisasse de config diferente — não é o caso. |

**Manifesto, não `readdir`:** o nome do arquivo não carrega o `layout` (chave de merge, com backslashes
inválidos em nome de arquivo), nem `eligibleForPollux`, nem `docType`. E a presença do manifesto é o
sinal de run completo. `readdir` leria XMLs de um run abortado no meio.

**Leitura do manifesto degrada, nunca estoura:** manifesto ausente/ilegível/`schemaVersion` desconhecida
⇒ `[]` + `console.warn`. Uma exceção em `setupNodeEvents` derruba o Cypress inteiro antes de qualquer
teste — e aí nem o `cypress-summary.json` é escrito.

`cy.task` continua sendo usado, mas só para o que é I/O de **escrita** durante o teste
(`appendResult`) e para o POST — nunca para descoberta.

---

## 5. Isolamento de falha e critério PASS/FAIL

### 5.1 Isolamento

1. **Um `it()` por candidato.** Mocha já isola: um `it()` que falha não impede os seguintes.
2. **Nada de I/O crítico em `before()`/`beforeEach()` de nível `describe`.** Uma exceção ali aborta o
   `describe` inteiro (todos os candidatos). Fixtures SOAP são leves e podem ficar no `beforeEach`;
   qualquer coisa que dependa de rede/disco externo, não.
3. **O comando Pollux precisa de uma variante que não lança.** O `cy.enviarNFeParaPolux` atual
   (`cypress/support/commands.js`) faz `throw new Error(...)` quando o Pollux não devolve protocolo —
   comportamento correto para a spec alpha, **errado** aqui: `sem protocolo` é `rejected` (dado), não crash.
   Adicione `cy.enviarNFeParaPolluxSoft(xml)` (ou `{ soft: true }`) que **sempre resolve** para
   `{ outcome, cStat, protocolo, mensagemGeral, mensagemItem, erro }`. Não altere o comando existente —
   a spec alpha depende do comportamento atual.
4. **Grave o resultado antes de assertar.** Ordem dentro do `it()`: obter veredito → `cy.task('appendResult')`
   → POST best-effort → só então o `expect`. Se o assert falhar, o dado já está em disco.
5. **`after:run`** (evento de `setupNodeEvents`) escreve o `cypress-summary.json` a partir do NDJSON.
   Roda mesmo com testes falhos.

### 5.2 PASS/FAIL agregado — o exit code do Job 2

**Decisão: o exit code do `cypress run` NÃO é o veredito do Job 2.** O wrapper ignora-o
deliberadamente e decide a partir do `cypress-summary.json`.

```bash
npx cypress run --spec cypress/e2e/ia-candidates-batch.cy.js || true   # exit code do Cypress é ignorado
node scripts/verdict.js "$RUN_DIR"                                      # ESTE define o exit do Job 2
```

| Situação | Veredito do Job 2 |
|---|---|
| Manifesto ausente/ilegível | **FAIL** (2) — Job 1 não entregou |
| `candidates: []` (run legítimo, nada elegível) | **PASS** (0) — nada a fazer |
| Todos com veredito, mesmo 100% `rejected` | **PASS** (0) — mediu o que tinha que medir |
| Qualquer `infra_error` | **FAIL** (1) — houve candidato sem veredito |
| NDJSON não escrito / summary ausente | **FAIL** (1) |

**Racional:** neste job o Cypress é **instrumento de medição**, não gate de qualidade. Um XSLT que gera
XML rejeitado é resultado válido — e é justamente a métrica de negócio da apresentação. Se o exit code
sinalizasse falha por rejeição, o `set -euo pipefail` do wrapper abortaria e o operador concluiria que o
job quebrou.

O `expect(cStat).to.eq("100")` **continua no `it()`** de propósito: mantém o relatório do Cypress
visualmente legível (verde = aceito, vermelho = rejeitado) para inspeção humana. Só não governa o exit code.

---

## 6. Runbook de provisionamento da VM (172.25.32.31)

> Executor: `@lp-devops` (Gage). **Não** executável nesta sessão (sem SSH).

### 6.1 Dois bloqueios a resolver ANTES de rodar qualquer comando

**B1 — `sudo` na VM.** A §6 do plano registra que o .NET SDK foi instalado em `~/dotnet` *"user-space,
sem sudo disponível na VM"*. As dependências de sistema do Cypress (`libgtk-3-0`, `libnss3`, `xvfb`, …)
**exigem `apt` ⇒ root**. Node e o binário do Cypress instalam-se sem sudo; as `.so` do Electron, não.
Se o sudo não vier, veja §6.5 (plano B).

**B2 — versão real do Ubuntu.** O hostname é `UBU220405RUN`, que sugere **22.04.5**, mas o pedido
menciona 24.04. Os nomes de pacote **diferem** (transição `t64` do 24.04) e o `apt install` falha inteiro
com um nome errado. Primeiro comando a rodar:

```bash
lsb_release -ds && uname -m && node --version 2>/dev/null; echo "sudo? "; sudo -n true 2>&1 | head -1
```

### 6.2 Dependências de sistema (escolha a lista pela saída acima)

```bash
# Ubuntu 22.04 (provável, dado o hostname)
sudo apt-get update && sudo apt-get install -y \
  libgtk-3-0 libgbm-dev libnotify-dev libnss3 libxss1 libasound2 libxtst6 xauth xvfb

# Ubuntu >= 24.04 (noble) — variantes t64
sudo apt-get update && sudo apt-get install -y \
  libgtk-3-0t64 libgbm-dev libnotify-dev libnss3 libxss1 libasound2t64 libxtst6 xauth xvfb
```

### 6.3 `xvfb` é necessário? **Sim — decisão fechada.**

"Headless" no Cypress significa "sem janela visível", **não** "sem servidor gráfico". O Electron do
Cypress em Linux ainda precisa de um X server. O Cypress detecta `DISPLAY` ausente e **sobe o Xvfb ele
mesmo** — mas exige o **binário instalado**; sem ele falha com `Your system is missing the dependency: Xvfb`.

**Consequência prática:** instale `xvfb`, mas **não** envolva o comando em `xvfb-run` no script. Deixe o
Cypress gerenciar; `xvfb-run` por fora costuma conflitar com o Xvfb interno e mascarar erros. Isso vale
especialmente sob cron, onde `DISPLAY` nunca está setado.

### 6.4 Node, Cypress e cache

**Node 22 LTS via NodeSource** (Cypress 15 exige Node 20.x / 22.x / ≥24.x; a `nodejs` do apt do 22.04 é
antiga demais). Instalação em `/usr/bin`, disponível para qualquer usuário — importante se o cron rodar
sob outro usuário:

```bash
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
sudo apt-get install -y nodejs
node --version   # v22.x
```

> **nvm foi rejeitado:** vive em `~/.nvm` e depende do `.bashrc`, que o cron **não carrega**. É a causa
> nº 1 de "roda no shell, quebra no cron" com Node. Se o sudo não vier, use um **tarball oficial em
> `~/node`** com `PATH` exportado explicitamente no script (§6.6) — nunca nvm.

**Versão do Cypress fixada.** O `package.json` hoje tem `"cypress": "^15.19.0"` — o `^` permite minor
novo silencioso numa VM que ninguém observa. Troque para exato (`"15.19.0"`) e **commite o
`package-lock.json`**; na VM use `npm ci`, nunca `npm install`.

**`CYPRESS_CACHE_FOLDER`.** O default é `~/.cache/Cypress`, que resolve pelo `HOME` do usuário. Se o cron
rodar sob usuário diferente do que instalou, o binário "some" e o Cypress tenta baixar 200 MB dentro do
cron. Fixe um caminho absoluto **no `.npmrc` do projeto e no script**:

```bash
export CYPRESS_CACHE_FOLDER=/home/elson/.cache/Cypress
```

### 6.5 Validação do provisionamento (nesta ordem)

```bash
cd ~/layoutparser-cypress
export CYPRESS_CACHE_FOLDER=/home/elson/.cache/Cypress
npm ci
npx cypress verify        # valida binário + libs de sistema. É AQUI que falta de libgtk/libnss3 aparece
npx cypress info          # confirma browsers detectados
env -i HOME=/home/elson PATH=/usr/bin:/bin CYPRESS_CACHE_FOLDER=/home/elson/.cache/Cypress \
  bash -lc 'cd ~/layoutparser-cypress && npx cypress run --spec cypress/e2e/ia-candidates-batch.cy.js'
```

> O último comando é o **teste que realmente importa**: `env -i` simula o ambiente mínimo do cron. Passar
> no shell interativo e quebrar no cron por `PATH`/`HOME` é o modo de falha mais comum deste tipo de job.

### 6.6 Se o `sudo` for negado — plano B

Os testes deste repo são **100% `cy.request`** (SOAP/HTTP); não há uma linha de interação de UI. Toda a
stack gráfica do Electron é overhead puro aqui. Sem sudo, o caminho é um **runner Node puro**
(`node scripts/run-batch.js`) que reusa a mesma lógica SOAP e escreve **exatamente os mesmos artefatos**
da §3 — zero dependência de sistema além do Node.

**Não implemente isso agora.** É plano B, acionado só se o sudo for negado. Mas **desenhe para ele**:
mantenha a lógica de I/O e POST em módulos Node simples (`cypress/support/lib/*.js`) chamados por
`cy.task`, não embutida na spec. Assim o plano B vira um `main()` novo, não uma reescrita.

---

## 7. `run-metrics-batch.sh` é síncrono? — **NÃO CONFIRMADO. O script não existe neste repo.**

Busca em todo o repositório: a string `run-metrics-batch` aparece **exclusivamente** em
`docs/architecture/plano-metricas-ia-servidor-producao.md`. O script existe **só na VM**
(`~/layoutparser-ai-metrics/run-metrics-batch.sh`), foi criado durante o deploy e **nunca foi
versionado**. Não é possível determinar daqui se ele bloqueia ou se auto-backgrounda.

> **Risco adicional, independente da resposta:** scripts de produção não versionados. Recomendo que
> `run-metrics-batch.sh`, `enable-metrics-job.sh`, `disable-metrics-job.sh` e o futuro wrapper passem a
> viver em `ai/XslSynth/scripts/` neste repo, com o deploy copiando de lá. Hoje a VM é a única cópia.

**Evidência a coletar na VM** (`@lp-devops` ou o usuário, com a chave `layoutparser_automation`):

```bash
# 1) O conteúdo — resolve a questão direto
cat ~/layoutparser-ai-metrics/run-metrics-batch.sh

# 2) Procura por auto-backgrounding
grep -nE '&\s*$|nohup|setsid|disown|systemd-run|screen|tmux' ~/layoutparser-ai-metrics/run-metrics-batch.sh

# 3) Prova empírica (mais forte que ler o script): mede o tempo de parede
time ~/layoutparser-ai-metrics/run-metrics-batch.sh --limit 1
#    ~2-5 min  => SÍNCRONO  (bloqueia até o fim; `&&` no wrapper funciona)
#    < 2 s     => ASSÍNCRONO (retornou antes de terminar; wrapper precisa de wait/lockfile)
```

**Como o wrapper se comporta em cada caso:**

- **Síncrono** (esperado, e consistente com a estimativa de 3–4 h da §6 do plano): o desenho do Gage vale
  como está — `set -euo pipefail` + chamadas sequenciais.
- **Assíncrono:** não recomendo lockfile/polling. A correção certa é **remover o backgrounding do próprio
  script** (o `cron` já roda em background por natureza; auto-backgroundar dentro de um cron job é
  redundante e impede exatamente este encadeamento). Um `wait` no fim resolve o caso trivial.

**Ajuste que o wrapper precisa independentemente disso** — o desenho da §7.5 usa `set -e` puro, o que faz
uma falha do Job 1 impedir o Job 2 *e* perder o registro. Mínimo necessário:

```bash
#!/usr/bin/env bash
set -uo pipefail                     # SEM -e: queremos tratar a falha, não abortar mudo
export PATH=/usr/bin:/bin:/usr/local/bin
export CYPRESS_CACHE_FOLDER=/home/elson/.cache/Cypress
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)"
export LP_METRICS_RUN_DIR="$METRICS_HOME/runs/$RUN_ID"

~/layoutparser-ai-metrics/run-metrics-batch.sh --run-id "$RUN_ID"; JOB1=$?
if [ $JOB1 -ne 0 ]; then echo "[wrapper] Job 1 falhou ($JOB1) — Job 2 não roda (fail-fast)."; exit $JOB1; fi
[ -f "$LP_METRICS_RUN_DIR/manifest.json" ] || { echo "[wrapper] manifesto ausente — abortando."; exit 2; }

~/layoutparser-cypress/run-cypress-batch.sh "$LP_METRICS_RUN_DIR"; exit $?
```

O `--run-id` explícito elimina a corrida de "qual run é o mais recente" entre os dois jobs — o wrapper
decide, ninguém adivinha.

---

## 8. Sequenciamento (o que destrava o quê)

| # | Dono | Entrega | Bloqueia |
|---|---|---|---|
| 1 | **Cass** (`@qa-cypress`) | Spec batch + tasks + scripts + `run-cypress-batch.sh`, contra o contrato §2/§3 | — (pode começar **já**; com `[]` a spec dá skip limpo) |
| 2 | **Lia** (`@lp-parser-llm`) | `MetricsBatchRunner` grava run dir + manifesto + XML aplicado (A1/A2) | 1 só produz dado real depois disto |
| 3 | **Dex** (`@lp-backend-dev`) | `POST /api/ai-metrics/generations/ingest` (A4) | painel só mostra dado depois disto |
| 4 | **Gage** (`@lp-devops`) | Provisionar VM (§6) + evidência do §7 + trocar a entrada do crontab | execução real na VM |

**O item 1 não depende de 2, 3 ou 4** — esse é o ponto do contrato. A Cass entrega contra o manifesto;
enquanto o Job 1 não o produzir, a spec roda, dá skip e escreve um summary com `total: 0`. Isso é
verificável hoje, no notebook, com um manifesto de exemplo escrito à mão.

**Rota de crescimento (pós-MVP, não agora):** o N do Job 2 cresce coletando mais **TXT de instância reais**
pareados com os TCLs do dataset — não sintetizando dados (§A3). Cada novo par TXT+TCL adiciona um candidato
elegível. A ordem de valor é: emissão NF-e (feito) → cancelamento/inutilização NF-e (exige encadear com
uma rejeição real, ver §2 do `CLAUDE.md` do repo Cypress) → CT-e/MDF-e (exige fixtures SOAP novas).
