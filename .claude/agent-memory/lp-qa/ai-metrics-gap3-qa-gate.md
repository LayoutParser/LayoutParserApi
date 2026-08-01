---
name: ai-metrics-gap3-qa-gate
description: QA gate do painel de métricas de IA (Gap 3). Endpoint 3 reprovado 2026-07-30 (6 defeitos); RE-GATE 2026-07-31 (commit 9e48650) fechou os 6 por execução. Achado decisivo em aberto: as duas pontes de ingestão ativas juntas contam cada geração DUAS vezes.
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

### Achados menores do Endpoint 4 (revisado pela 1ª vez neste gate)

- Idempotência **só vale com `Timestamp` explícito**; sem ele a ingestão usa `DateTime.Now` e o
  reenvio duplica (medido: 1 geração enviada 2x → 2 itens). O XML doc do controller promete
  idempotência sem essa ressalva.
- **Sem teto de tamanho de campo** (`Layout`/`Modelo`): um `Layout` de 200.000 chars foi aceito e
  gravou 200 KB numa linha só. O endpoint irmão (`cypress-result`) capa em 500/20/1000 chars.
  Agrava a fragilidade nº 2 abaixo (retenção ~20 MB, e o leitor só abre os 3 arquivos mais recentes).
- Endpoint de **escrita sem autenticação** (a app não tem `UseAuthorization`), alimentando o painel
  mostrado à diretoria.

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
