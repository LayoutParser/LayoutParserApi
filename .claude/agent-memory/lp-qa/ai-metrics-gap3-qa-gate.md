---
name: ai-metrics-gap3-qa-gate
description: QA gate do painel de métricas de IA (Gap 3). Endpoint 3 (cypress-result) reprovado em 2026-07-30 com 6 defeitos reproduzidos em harness; lições estruturais sobre "log como banco de dados" e sobre dois Serilog independentes escrevendo no MESMO arquivo.
metadata:
  type: project
---

Painel de métricas de IA (Gap 3, `docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md`)
foi apresentado ao coordenador/diretoria a partir da rodada de **sábado 2026-08-01**. Gate do
Endpoint 3 (`POST /api/ai-metrics/cypress-result`, commit `a1df178`) deu **FAIL** em 2026-07-30.

**Why:** número errado exibido em apresentação de diretoria é severidade máxima — e três dos
defeitos faziam justamente isso (rejeição contada como autorização, geração fantasma no gráfico,
cStat forjado por texto livre honesto).

**How to apply:** ao revisar qualquer coisa nesta área, atacar primeiro as três fragilidades
ESTRUTURAIS abaixo — os defeitos pontuais são sintomas delas e voltam em cada feature nova.

### 1. Dois Serilog independentes escrevem no MESMO arquivo, com templates divergentes
`ai/XslSynth` (job `metrics-batch`) e a API configuram loggers separados apontando para
`layoutparserapi.log`. Os `outputTemplate` divergiram (o do job não tem `[Corr:...]`), e o regex
do leitor exigia o campo → 100% das linhas de geração viraram "continuação" e o painel ficava
vazio. **Nenhuma revisão só-de-código pega isso**: os dois lados parecem certos isoladamente.
Sempre que alguém tocar em template de log ou no regex do leitor, rodar o parser contra linhas
reais dos DOIS produtores. Mesma classe do bug de `DateTimeStyles` em
[[unified-logging-parse-bug-and-log-dir-incident]].

### 2. Arquivo de log rotativo usado como banco de dados
O merge do Cypress é lógico, na leitura: o resultado da validação só existe como linha de log.
Mas o Serilog rotaciona (`fileSizeLimitKB`/`retainedFileCountLimit`) e o leitor só abre os 3
arquivos mais recentes por fonte — ou seja, **estado de negócio guardado num buffer circular**,
que some sozinho sem erro nenhum. Aceitável como atalho de prazo; não aceitar como permanente.

### 3. Campo de texto livre logado em mensagem tokenizada por espaço
O parser faz `split(' ')` + `fields[key] = value` (último vence). Qualquer campo livre no fim da
mensagem (`Observacao`) sobrescreve os campos reais — inclusive sem má-fé: a observação natural
"Rejeitado: esperava CStatPollux=100 e veio 110" faz o painel exibir cStat 100. Com `\n` no
texto, forja uma LINHA inteira (geração fantasma com métricas arbitrárias). O irmão
`LogsController.PostClientLog` já sanitiza (achata `\r\n`, trunca) — este endpoint não seguiu.

### Lição de processo (minha)
Aprovei os Endpoints 1 e 2 deste mesmo Gap sem executar o parser contra as linhas reais do
`Logs/layoutparserapi.log`, e por isso deixei passar o defeito nº 1, que zerava o painel inteiro.
Para feature de parsing/log, **revisão de código não substitui execução contra dado real** —
harness console com `ProjectReference` pro `.csproj` da API resolve em minutos (receita em
[[unified-logging-parse-bug-and-log-dir-incident]]).
