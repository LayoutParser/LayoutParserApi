# ADR — Backfill proativo do catálogo Sysmiddle + retraining automatizado (F4)

Autor: `@lp-architect` (Aria). Pedido do dono, 2026-09-08. Estende
[`adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md`](adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md)
(issue #151), cujas fases F1/F2/F3 já estão implementadas e em produção (PRs #339/#343). Aquele
ADR deixou F4 (retraining automatizado) explicitamente fora de escopo — este ADR resolve F4 e
também a ideia nova do dono: rodar o loop de convergência em lote sobre o catálogo já existente
("backfill"), em vez de só reativamente quando um documento chega para parse.

## 1. Veredito curto

- **Retraining automatizado (F4): viável, desenho abaixo.** Depende só de agendamento e exclusão
  mútua com o Ollama de inferência — nenhuma peça nova de infraestrutura.
- **Backfill em lote sobre "todos os layouts/mappers do catálogo": NÃO viável hoje, e a causa
  não é custo de CPU — é ausência de corpus real de entrada.** O gargalo já foi medido
  independentemente nesta mesma linha de trabalho (Gap 3 de `ai-metrics-job1-job2-gaps`, ver
  §3): de 54 pares candidatos, só 4 têm alguma chance de instância real, e mesmo esse único TXT
  de instância existente **não bate** com o schema TCL do dataset. Rodar backfill "no catálogo
  inteiro" hoje significaria gerar convergência com um documento de entrada fabricado — o
  problema que o dono já rejeitou explicitamente ao recusar sintetizar instância para o Job 2.
  Recomendo a versão pequena e honesta: começar por 1 layout com corpus real disponível, medir
  custo real, e usar F3 (captura incremental já em produção) como o mecanismo de crescimento
  orgânico do corpus em vez de forçar backfill sem dado.

## 2. Escala real do catálogo (medida no código, não estimada)

`Services/Database/LayoutDatabaseService.cs` (linhas 76-103): a query de listagem de layouts é
`SELECT TOP (200) ... FROM tbLayout WHERE ProjectId = 2` — o catálogo deste projeto no SQL
compartilhado é limitado a 200 por design da própria query (não é um teto artificial imposto por
mim; é o `TOP` já hardcoded). Ou seja, o piso é "até 200 layouts", não milhares.

`Services/Database/MapperDatabaseService.cs` (linhas 41-62), usado por
`CachedMapperService.GetAllMappersAsync` no warmup de cache: **este método não tem `WHERE
ProjectId`** — lê `tbMapper` inteiro, sem filtro de projeto. Achado incidental relevante para
este ADR: se "enumerar o catálogo de mappers" for feito reaproveitando este método como está,
o resultado inclui mappers de outros times na credencial SQL compartilhada (`ConnectUS_Macgyver`,
~231.890 times, ver `.claude/rules/security.md`), não só os deste projeto. Já existe o método
certo para escopo correto — `MapperDatabaseService` linha ~284, "Busca mapeadores por
layoutGuid restringindo por ProjectId e PackageGuid (lista permitida)" — usar esse padrão de
filtro (`ProjectId`/`AllowedPackageGuids`), nunca `GetAllMappersAsync` sem filtro, para qualquer
enumeração de backfill. Sinalizo isto como achado de segurança/escopo, não decisão de design —
`GetAllMappersAsync` continua correto para seu uso atual (warmup de cache local do processo, que
é read-only e não expõe nada), só não deve ser reaproveitado como está para uma feature nova que
itera e dispara trabalho por mapper.

**Conclusão de escala:** o catálogo real deste projeto é pequeno (≤ 200 layouts, mappers
filtráveis pela mesma faixa). Isso é uma boa notícia para viabilidade de CPU — o teto não é
"milhares de convergências", é baixas centenas. O bloqueio não é escala, é corpus (§3).

## 3. Por que "gerar volume rodando o catálogo inteiro" não funciona hoje

Investigação decisiva já existe, feita nesta mesma linha (Job 1→Job 2, `ai-metrics-job1-job2-gaps`,
2026-07-30, ver `.claude/agent-memory/lp-architect/ai-metrics-job1-job2-gaps.md` e
`docs/architecture/handoff-job2-cypress-batch.md` §A3):

- O dataset held-out tem 54 pares `TCL(schema) → XSLT`. Só 4 produzem raiz `<NFe>` (o resto é
  retorno SEFAZ→ERP, consulta, CT-e/MDF-e — formatos sem instância de entrada disponível de
  qualquer forma).
- O único TXT de instância real conhecido (`nfe-emissao-normal.mq_series.txt`, layout
  `LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe`) usa blocos `LINHA001`; o TCL do dataset usa `LINE
  identifier="A"` — **não casam**. Ou seja, mesmo esse único caso "elegível" não tem, hoje, um
  documento de entrada que o `RepairOrchestrator` consiga rodar de ponta a ponta sem intervenção.
- Sintetizar instância foi **rejeitado** no mesmo achado: a SEFAZ-fake (Pollux) rejeita por
  chave/DV/CNPJ — sintético mede o gerador de dado, não o XSLT. O mesmo raciocínio se aplica
  aqui: gerar um `InputContent` fabricado para os ~196 layouts restantes do catálogo (que nunca
  tiveram parse real) produziria convergência contra um documento que não representa o mundo
  real — o XSLT resultante "converge" mas contra um alvo artificial, e entra no dataset de
  treino (F3) como se fosse produção real. Isso contamina o dataset com o mesmo risco que a §5.5
  do ADR anterior já sinalizou para seed ruim, só que pior: lá o seed vinha de um gabarito
  Sysmiddle real; aqui o próprio *input* seria fabricado.

`RepairOrchestrator` (`ai/XslSynth.Core/Core/RepairOrchestrator.cs`) recebe `input` como
parâmetro — não há, em lugar nenhum do código investigado, uma fonte de `InputContent` de
amostra por layout persistida no catálogo (`tbLayout`/`tbMapper` não têm coluna de sample; não
existe `SampleDocumentService` nem equivalente). O único jeito honesto de obter `input` real por
layout é: (a) tráfego de produção real que já passou por parse (é o que F1/F2/F3 já capturam,
reativamente), ou (b) pedir ao dono/usuário uma amostra manual por layout — não escalável para
"backfill em lote automático" como pedido.

## 4. Custo por convergência — o que dá para afirmar com honestidade

Não encontrei, no código investigado, uma métrica de duração persistida especificamente para
`RepairOrchestrator.RunAsync` em produção (F1/F3 não logam `DurationSeconds` — só o `MetricsBatchRunner`
do CLI offline o faz, para o caso não-seedado). O que existe de medição real é do CLI offline
(`MetricsBatchRunner`, mesmo arquivo de onde vem o achado do §3): cada geração loga
`TokensPorSegundo`/`DuracaoSegundos` por caso, em CPU (`layoutparser-sysmiddle-dsl:1.5b`,
hardware `BRNDDAPPBLD01`-equivalente, ver `production-server-hardware.md`), mas não tenho no
código um número agregado de "segundos por convergência completa" à mão para citar sem inventar
— seria estimativa não lastreada, que as instruções deste ADR pedem para evitar. **Recomendação
prática:** antes de qualquer decisão de agendamento fino para backfill, rodar 1 convergência real
medida (F1 seedado ou não) e registrar `DurationSeconds`/`Iterations` no log estruturado
já existente (`AiMetrics`) — é a mesma telemetria que o CLI já usa, só falta ligá-la ao caminho
de produção do `RepairOrchestratorXslSynthesizerService`. Isso é uma tarefa pequena e
recomendo tratá-la como pré-requisito do backfill-piloto (§5), não como parte deste ADR.

O que sei com confiança, sem precisar da medição acima, é a restrição de **concorrência com o
Ollama de inferência**: a VM `172.25.32.5` roda o Ollama de produção (usado por F1/toda
convergência em runtime) e, se F4 (retraining) rodar na mesma máquina, os dois competem por CPU
inteira — `train_lora.py` já é conhecido por rodar ~40h em CPU nesta mesma VM (ver
`.claude/agent-memory/lp-architect/fine-tuning-nichado-ollama-2026-09-02.md`). Rodar retraining e
inferência de produção ao mesmo tempo degradaria toda convergência em runtime (F1) pela duração
inteira do treino — isso não é aceitável para uma dependência que a API trata como síncrona o
suficiente para bloquear resposta de parse em caso de timeout duro (mesmo sendo fire-and-forget,
um Ollama lento arrasta o backlog de candidatos pendentes).

## 5. Recomendação — backfill não em escala, piloto pequeno primeiro

Não recomendo abrir uma issue de "backfill em lote sobre o catálogo inteiro" agora — os dados não
sustentam essa resposta (§3). Recomendo, em vez disso:

**Piloto de 1 layout** (não é backfill de catálogo, é prova de conceito):
1. Escolher **1 layout com TXT de instância real já disponível** (candidato natural:
   `LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe`, mesmo com o mismatch de schema `LINHA001` vs `LINE
   identifier="A"` documentado no §3 — corrigir esse mismatch específico é trabalho pequeno e
   symptomatic do problema real, então vale resolver antes de generalizar).
2. Rodar `RepairOrchestrator` manualmente contra esse par real, medir `DurationSeconds`/
   `Iterations` (telemetria do §4), confirmar que converge com o corpus real disponível.
3. Só depois de ter 1 caso medido de ponta a ponta, decidir se vale escalar para os demais
   layouts do catálogo que também tenham TXT de instância real conhecida — **não para os que não
   têm**, que continuam dependendo de F3 (captura incremental) crescer organicamente com tráfego
   real, como já decidido no ADR anterior.

Isso não é "recusar o pedido do dono" — é entregar a parte que os dados sustentam (retraining
automatizado, §6) e ser explícito sobre a parte que não sustentam ainda (backfill em escala),
com um caminho concreto e pequeno para destravá-la (corpus real por layout, um de cada vez, não
uma corrida automática sobre 200 entradas sem documento).

## 6. F4 — Retraining automatizado

### 6.1 Gatilho

Recomendo **volume acumulado, não agenda fixa** como gatilho primário, com um teto de agenda
como rede de segurança:

- **Gatilho por volume:** a cada N exemplos novos capturados por F3 (hook em
  `RepairOrchestratorXslSynthesizerService`, já em produção desde o ADR anterior) desde o último
  treino, dispara `train_lora.py`. N sugerido: mesma ordem de grandeza do dataset atual
  (6.044 exemplos em `ai/XslSynth/training-data/sysmiddle-dsl-dataset-2026-09-02.jsonl`) não é o
  parâmetro certo para incremento — F3 cresce devagar (convergência real em produção, não
  batch), então N deveria ser bem menor (ex.: 200-500 exemplos novos) para o retraining não ficar
  represado indefinidamente esperando um volume que pode levar meses para se acumular
  organicamente.
- **Teto de agenda (rede de segurança):** se o volume não bater N em, digamos, 90 dias, dispara
  mesmo assim com o que houver acumulado — evita o cenário "nunca retreina porque o volume nunca
  bate", que seria pior que não ter F4.
- Ambos os gatilhos escrevem para a mesma fila/flag — não precisa de dois mecanismos paralelos,
  só duas condições que OR-am para a mesma ação "disparar treino".

### 6.2 Exclusão mútua com o Ollama de inferência (crítico)

Não é opcional — é a restrição dominante do desenho, dado o hardware (§4). Mecanismo recomendado,
do mais simples ao mais robusto:

- **Mínimo viável:** um lock de arquivo (`retraining.lock`) na VM, checado por (a) o cron/gatilho
  de retraining antes de começar, e (b) opcionalmente um alerta se o lock persistir além da
  duração esperada (~40h + margem). Não chama Ollama enquanto o lock existir.
- **Janela de baixo tráfego:** agendar a checagem do gatilho para só efetivamente disparar em
  horário de menor uso do Ollama de produção (madrugada/fim de semana) — reduz a chance de um
  treino de 40h colidir com pico de parse real, mesmo que não elimine o risco (40h atravessa
  qualquer janela).
- **Não recomendo** tentar rodar os dois com prioridade de processo (nice/cgroups) para
  "compartilhar" a CPU — em hardware CPU-only sem folga (i7-4790 Haswell, sem GPU, ver
  `production-server-hardware.md`), isso degradaria os dois para uma faixa inútil em vez de dar
  a cada um seu turno limpo. Exclusão mútua total (só um dos dois roda por vez) é mais simples e
  mais previsível.

### 6.3 Validação pós-treino antes de promover o modelo

F4 não deveria trocar o modelo em produção (`layoutparser-sysmiddle-dsl:1.5b` ou o que estiver
ativo) automaticamente sem checagem — mesmo risco que backfill sem curadoria (§7). Recomendo:
após o treino, rodar o mesmo held-out set usado hoje para medir o modelo atual (54 pares/N
elegíveis) contra o modelo novo, e só promover se as métricas não regredirem. Isso reaproveita
a infraestrutura de medição que já existe (`MetricsBatchRunner`, `--mode=metrics-batch`) — não é
peça nova, é reordenar quando ela roda (pós-treino, antes de promover, em vez de só ad-hoc).

## 7. Curadoria — por que F3 pode pular supervisão humana e um F4/backfill hipotético não pode do mesmo jeito

Pergunta do dono (via instrução deste ADR): backfill automático deveria pular a curadoria humana,
já que convergência 1:1 contra o Sysmiddle já é uma validação forte?

**Resposta, com o raciocínio explícito:** para F3 (captura incremental, já em produção), sim — a
convergência contra `CanonicalDiffer.Diff == 0 && xsd.IsValid` (ADR anterior, §3.1) é uma validação
estrutural forte porque compara contra um gabarito **real** (Sysmiddle rodando o mesmo documento
real). O input e o gabarito são ambos do mundo real; só o XSLT é gerado. Isso é diferente de
correção humana (#345/#346), que é julgamento subjetivo de um humano sobre um campo — por isso
aquele fluxo exige curadoria antes de virar dado de treino, e este não.

Só que essa mesma garantia **depende do input ser real** — é exatamente o que falta para
backfill em escala (§3). Se o input fosse fabricado (documento sintético para preencher um layout
sem tráfego real), a convergência 1:1 deixaria de validar contra o mundo real e passaria a
validar contra a própria fabricação — um viés circular, não uma validação forte. Por isso a
resposta não é uniforme: **exemplos que nascem de F3 (input real, gabarito real) não precisam de
curadoria humana adicional; um hipotético backfill com input fabricado precisaria, no mínimo, de
alguém confirmar que o documento fabricado é representativo antes de qualquer convergência dele
entrar no dataset** — que é mais um motivo para não fazer backfill em escala sem corpus real, em
vez de tentar compensar com curadoria depois.

## 8. Fases propostas

| Fase | Escopo | Depende de |
|---|---|---|
| **F4.1 — Telemetria de duração no caminho de produção** | Logar `DurationSeconds`/`Iterations` do `RepairOrchestrator` em runtime (hoje só o CLI mede isso) — pré-requisito para qualquer decisão de agendamento fino. | Nada — aditivo, pequeno. |
| **F4.2 — Gatilho de retraining por volume + teto de agenda** | Contador de exemplos novos desde o último treino (reaproveitando o hook de F3); dispara `train_lora.py` ao bater N ou o teto de 90 dias. | F4.1 (para calibrar N com dado real), F3 (já existe). |
| **F4.3 — Exclusão mútua + validação pós-treino** | Lock de arquivo na VM + checagem de held-out antes de promover o modelo novo. | F4.2. |
| **Piloto de backfill (1 layout, não é fase formal)** | Rodar `RepairOrchestrator` manualmente contra 1 par real com TXT de instância conhecido, medir e documentar. Pré-condição para qualquer conversa futura sobre backfill maior. | F4.1 (mesma telemetria). |

**Não recomendo** uma fase "backfill em lote sobre o catálogo" neste ADR — os dados não
sustentam esse escopo hoje (§3, §5). Se o corpus real crescer (mais TXTs de instância
disponíveis, seja por captação manual do dono ou por mais tráfego real via F3), revisar esta
decisão faz sentido — não é definitivo, é "não com o que temos agora".

## 9. Recomendação de issue

Recomendo ao `@lp-pm`:
1. Uma issue para **F4 completo (F4.1+F4.2+F4.3)** — retraining automatizado, referenciando este
   ADR e o ADR anterior (#151). Dono de implementação: `@lp-parser-llm` (motor de treino) com
   apoio de `@lp-devops` (agendamento na VM, lock de arquivo).
2. **Não recomendo** issue para "backfill em lote" — não é uma tarefa de implementação ainda, é
   uma dependência não resolvida (corpus real por layout). Se o dono quiser formalizar o piloto
   de 1 layout como tarefa pequena, pode entrar como sub-tarefa da issue de F4 (mesma telemetria,
   F4.1), não como issue própria.

## 10. Resumo executável para `@lp-parser-llm` / `@lp-devops`

- **F4.1:** adicionar log estruturado (`AiMetrics`, mesmo `Source` já usado pelo CLI) de
  `DurationSeconds`/`Iterations` em `RepairOrchestratorXslSynthesizerService`, no ponto onde
  `report` é recebido de `RunAsync` — reaproveitar o padrão de log já usado no ADR anterior (F3),
  não inventar sink novo.
- **F4.2:** contador de exemplos desde o último treino (pode ser um arquivo/contador simples ao
  lado do JSONL de F3, ou consulta ao tamanho do arquivo); gatilho dispara `train_lora.py` via
  cron/script já existente na VM quando N (calibrar após F4.1) ou 90 dias, o que vier primeiro.
- **F4.3:** lock de arquivo (`retraining.lock`) checado antes de qualquer chamada ao Ollama em
  runtime **e** antes do treino iniciar; após o treino, rodar `MetricsBatchRunner
  --mode=metrics-batch` contra o held-out atual e só promover o modelo novo (troca de tag/symlink
  no Ollama) se as métricas não regredirem.
- **Não implementar** "listar todos os mappers do catálogo sem filtro" reaproveitando
  `MapperDatabaseService.GetAllMappersAsync` como está — é um método real que lê `tbMapper` sem
  `WHERE ProjectId`, correto para seu uso atual (warmup local read-only) mas não deve virar base
  de uma feature que dispara trabalho por mapper enumerado; usar o método já filtrado por
  `ProjectId`/`AllowedPackageGuids` (linha ~284 do mesmo arquivo) se algum dia essa enumeração
  for necessária.
- **Não implementar** backfill em lote sobre o catálogo inteiro agora — corpus real insuficiente
  (§3). Revisar só se o corpus real crescer.

---
*ADR de `@lp-architect` — análise, sem implementação. Push/PR e criação de issue ficam com
`@lp-devops`/`@lp-pm`.*
