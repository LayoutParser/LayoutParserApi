---
name: ai-metrics-gap3-qa-gate
description: QA gate do painel de métricas de IA (Gap 3). 6 defeitos fechados em 9e48650; Handoff 1 (hardening, e6df0b7) aprovado CONCERNS por matriz de mutação. Achado decisivo em aberto: as duas pontes de ingestão ativas juntas contam cada geração DUAS vezes.
metadata:
  type: project
---

Painel de métricas de IA (Gap 3, `docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md`)
apresentado à diretoria a partir da rodada de sábado **2026-08-01**.

### RE-GATE 2026-07-31 (commit `9e48650`, mergeado em master via PR #13) — 49 PASS / 1 FAIL

Os **6 bloqueios** do gate anterior estão **fechados**, verificados por execução em harness isolado
(nunca apontando pro diretório de log de produção): B1 `[Corr:]` opcional (validado com as linhas
REAIS do job), 4ª fonte `layoutparserai.log`, B3 sanitização de CR/LF, B2 regex ancorado pro
`Observacao` com `=`, B4 `TotalCStatAutorizado` por `CypressValidado`, B6 merge só na geração mais
recente anterior ao POST, B5 merge sobrevive ao recorte de período. Build: 0 erros / 543 warnings
(passivo pré-existente).

### 🔴 Achado estrutural em aberto — contagem em dobro pelas duas pontes

Existem DOIS caminhos para a mesma geração chegar ao painel: (a) `POST /api/ai-metrics/generations/ingest`
(Endpoint 4, grava em `layoutparserapi.log` COM campo `Timestamp=`) e (b) cópia do arquivo da VM
lida como 4ª fonte (`layoutparserai.log`, SEM campo `Timestamp=` — `MetricsBatchRunner.LogCaso` não
o emite). A dedup é `GroupBy((Layout, Timestamp))` em `AiMetricsReaderService`, e o `Timestamp` de
(b) é o instante em que a LINHA foi escrita, enquanto o de (a) é o instante enviado no payload.

**Só colapsa se os dois baterem ao milissegundo E na mesma base de fuso.** Medido: iguais → 1;
1,3s de diferença → 2; VM em UTC com API em UTC-3 → 2; rodada real de 54 casos → **108**. Pior
efeito: com o Cypress aprovando os 54, o painel exibe `54/108 = 50%` de aprovação quando o real é
100% — **número errado em apresentação de diretoria**, que é a severidade máxima deste projeto.
**Why:** a dedup foi desenhada para reenvio do MESMO produtor (retry/replay), não para dois
produtores com relógios independentes. **How to apply:** ativar **uma** ponte só; se as duas
forem necessárias, a chave de dedup precisa ser um id de geração estável (ou o `Timestamp` da VM
propagado idêntico nos dois caminhos), não o instante da linha.

### GATE do Handoff 1 — hardening (commits 946d24b..e6df0b7), 2026-07-31 — CONCERNS

Os 3 achados menores do Endpoint 4 (idempotência sem `Timestamp`, sem teto de campo, escrita sem
auth) foram **fechados** e verificados por execução. Suíte nova `tests/LayoutParserApi.Tests`
(29 testes) validada por **matriz de mutação**: 13 de 15 bugs reintroduzidos foram pegos.

**Os 2 buracos que a mutação revelou** (ambos com correção de 1 linha já validada por mim):
1. `AiMetricsReaderService.ApplyCypressMerge` — remover `.Where(g => g.Timestamp <= cypress.Timestamp)`
   **não quebra nenhum teste**. Esse limite superior impede que o resultado da rodada N marque a
   geração da rodada N+1 (que ninguém validou). Todos os cenários da suíte têm a geração ANTERIOR
   ao POST.
2. `AiMetricsRoundTripTests` não asserta `CStatPollux` — gravar `(null)` em vez de `null` passa
   despercebido, e o painel exibiria a string `(null)` como código cStat (`ParseNullableString` só
   mapeia o literal `"null"`).

**Divergência MEL × Serilog é DUPLA** (o `CapturingLogger` documenta só metade): MEL renderiza
`XsdValido=(null) ... Sucesso=True`; o Serilog real grava `XsdValido=null ... Sucesso=true`.
Nenhuma asserção atual depende disso, mas quem escrever teste novo contra o texto da mensagem
usando `CapturingLogger` codifica expectativa errada. **How to apply:** teste sensível a FORMATO
de linha vai no `AiMetricsRoundTripTests` (Serilog real), nunca no `CapturingLogger`.

**Fail-closed × painel React:** confirmado por execução nas duas configurações (com e sem chave)
que `GET generations` e `GET summary` continuam 200 sem header — o filtro está só nos 2 POSTs via
`[ServiceFilter]`, sem registro global. Mas `ci-dev.yml` **não provisiona** `AiMetrics__IngestApiKey`:
quem for chamar os endpoints de escrita toma 403 até o operador criar a env var.

### As 3 fragilidades ESTRUTURAIS (atacar primeiro — defeitos pontuais são sintomas delas)

1. **Dois Serilog independentes escrevem no MESMO arquivo, com templates divergentes.** Foi o que
   zerou o painel (regex exigia `[Corr:]`, template do job não tem). Nenhuma revisão só-de-código
   pega isso: os dois lados parecem certos isoladamente. Sempre rodar o parser contra linhas reais
   dos DOIS produtores. Mesma classe do bug de `DateTimeStyles` em
   [[unified-logging-parse-bug-and-log-dir-incident]].
2. **Arquivo de log rotativo usado como banco de dados.** Estado de negócio (resultado do Cypress,
   histórico de gerações) só existe como linha de log, num buffer circular
   (`fileSizeLimitKB` 2049 × `retainedFileCountLimit` 10 ≈ 20 MB) — e o leitor abre só os **3**
   arquivos mais recentes por fonte. Some sozinho, sem erro. Aceitável como atalho de prazo; não
   como permanente.
3. **Campo de texto livre logado em mensagem tokenizada por espaço.** Corrigido no `Cypress validado.`
   (regex ancorado + sanitização), mas `TryParseGeracao` **ainda** usa `split(' ')` + last-wins — a
   proteção hoje é o Endpoint 4 recusar espaço no `Layout`. Qualquer campo livre novo na linha de
   geração reabre o vetor.

### Lição de processo (minha)

Aprovei os Endpoints 1 e 2 sem executar o parser contra linhas reais e por isso deixei passar o
defeito que zerava o painel. Para feature de parsing/log, **revisão de código não substitui execução
contra dado real**. Receita do harness isolado em [[unified-logging-parse-bug-and-log-dir-incident]].
Detalhe operacional desta máquina: o Defender bloqueia o build de assembly chamado `qagate`
(`Access to the path ... denied` no copy obj→bin) — renomear o `AssemblyName` resolve; e usar
`<UseAppHost>false</UseAppHost>` evita o erro `CreateAppHost` em diretório temporário.
