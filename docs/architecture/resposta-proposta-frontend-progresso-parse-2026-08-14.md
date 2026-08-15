# Resposta à proposta de progresso real em `/api/parse/upload`

**De:** `@lp-architect` (Aria) · **Para:** equipe front-end (LayoutParserReact) · **Data:** 2026-08-14

## Recomendação

**Nenhuma das três (A/B/C) como endpoint novo. Variante: reaproveitar o padrão de ticket que já
existe no backend, não inventar um quarto mecanismo.**

Hoje o backend já tem **dois** pathways assíncronos por ticket dentro do mesmo `ParseController`/
`TransformationExecutionController`:

1. `transformationsTicket` → `GET /api/parse/transformations/{ticket}` (pathway low-code, existe
   desde a spec §2.6, usado desde `852df63`-era).
2. Pathway IA → `POST execute-candidates` (ticket) → `GET execute-candidates/{ticket}/ia-status`
   (issue #40, endurecido nesta mesma sessão: TTL, correção de race condition em `AiCandidateStore.Set`).

A proposta B do front, do jeito que está desenhada (`POST /api/parse/upload` → 202 + jobId,
`GET /api/parse/{jobId}/status`, `GET /api/parse/{jobId}/result`), criaria um **terceiro shape**
de "ticket assíncrono" no mesmo backend — cada um com semântica de status diferente
(`stage: queued|parsing|transforming|completed|failed` vs. `status: processing|completed` vs. o
enum próprio do `AiCandidateStatus`). Isso é o achado mais importante desta análise: **o problema
real não é falta de mecanismo, é fragmentação de um mecanismo que já existe duas vezes.** Um
terceiro contrato paralelo aumenta a superfície de manutenção sem resolver nada que o padrão atual
não resolva — e ainda obriga o front a aprender 3 shapes de polling em vez de 1.

## O que realmente falta (e é pequeno)

`POST /api/parse/upload` já **é** parcialmente assíncrono — só que só para a *transformação*, não
para o *parse* em si:

- O parse (`_parserService.ParseAsync` + `ReestruturarLayout`/`ReordenarSequences`/
  `BuildDocumentStructure`) roda **sempre síncrono, dentro da mesma request HTTP**. Não há teto de
  tempo, não há cancelamento, não há ticket — é o corpo principal do método `Upload`
  (`Controllers/ParseController.cs:127-189`).
- A transformação low-code (`_lowCodeAuto.RunAsync`) **já** tem teto síncrono configurável
  (`LowCode:SyncDeliveryTimeoutSeconds`, default 6s) e, se estourar, devolve
  `transformationsStatus: "processing"` + `transformationsTicket` consultável via
  `GET /api/parse/transformations/{ticket}` (`ParseController.cs:191-311`).

Ou seja: **a barra trava em 100% não porque falte um mecanismo de progresso — falta o front
consultar o mecanismo que já existe para a metade lenta (transformação), e a outra metade (parse)
não tem motivo documentado para ser lenta o suficiente para precisar de um.**

## Respostas às 6 perguntas

**1. `percentage` real é viável, ou só `stage` é realista?**
Só `stage`/enum é realista, e é o que os dois pathways existentes já fazem —
nenhum deles expõe percentual granular. O parse (`ParseAsync` → `ReestruturarLayout` →
`BuildDocumentStructure`) não tem etapas instrumentadas com contagem de itens; a transformação
low-code roda candidatos via runner externo (`.exe`) cuja progressão interna a API não observa.
Construir percentual real exigiria instrumentar cada etapa com contadores — custo desproporcional
para o problema. Recomendação: **enum de stage**, igual ao padrão já em uso (`not_applicable` |
`completed` | `processing` | `error`, ajustado para incluir `queued`/`parsing` se decidirmos
partir o parse do request principal — ver "Escopo" abaixo).

**2. Onde o estado do job vive, e por quanto tempo fica consultável?**
Já respondido pelos dois precedentes: `LowCodeTransformationStore` (índice em disco, consultável
entre restarts) e `AiCandidateStore` (`ConcurrentDictionary` em memória + arquivo em disco como
fonte de verdade, TTL configurável via `AiTransformationCandidate:TicketTtlHours`, limpeza por
`AiCandidateStoreCleanupBackgroundService`). Qualquer novo ticket (se o parse vier a precisar de
um) deve seguir o **mesmo** padrão: disco como fonte de verdade (sobrevive a restart do processo
IIS/Kestrel), memória como cache de leitura rápida para polling, TTL explícito — não reinventar.

**3. Cancelamento real ou só esconder o resultado?**
No pathway low-code já existente, cancelamento é **cooperativo, não real**: o `CancellationTokenSource`
do teto síncrono cancela o `Task` do lado da API, mas o processo `.exe` do LowCode runner externo
tem "sua própria janela" de encerramento (comentário no código, `ParseController.cs:237-239`) —
ou seja, mesmo hoje o kill não é instantâneo. Para o parse em si (que roda in-process, sem `.exe`
externo), um `DELETE` real de cancelamento é mais viável tecnicamente, mas **ainda não está
implementado em lugar nenhum do backend hoje** — nenhum dos dois pathways existentes expõe um
`DELETE`. Se vocês precisam de cancelamento de verdade (não só "parar de mostrar"), é escopo novo,
não reaproveitamento do que já existe.

**4. Múltiplas instâncias da API: job precisa ser consultável de qualquer instância?**
Hoje **não** — nenhum dos dois pathways existentes usa Redis/store compartilhado para os tickets;
ambos são por instância (disco local + memória local do processo). Isso já é uma limitação
conhecida e aceita nesses dois pathways (volume baixo, sessão curta). Se produção rodar múltiplas
instâncias atrás de um load balancer sem sticky session, o polling pode cair numa instância
diferente da que processou — isso já é verdade **hoje** para o pathway de transformação low-code
existente, então não é uma regressão nova introduzida pela proposta, é um risco pré-existente que
merece registro (ver "Se isto virar prioridade" abaixo), não bloqueio desta decisão.

**5. Intervalo de polling recomendado?**
Sem medição formal de tempo de parse documentada em log/teste/memória de projeto até esta sessão —
não vou inventar número. O que existe de referência indireta: o teto síncrono da transformação
low-code é 6s por default, e o design dele já assume que "a maioria termina dentro do teto" (senão
o timeout seria maior). Isso sugere que o parse puro (sem a transformação) é bem mais rápido que
isso — é síncrono hoje e ninguém reportou lentidão perceptível nele especificamente, só na
transformação. Recomendação: **medir antes de prometer um número** — instrumentar
`_parserService.ParseAsync` com um log de duração (`Stopwatch`) é trivial e não exige nenhuma
mudança de contrato; peçam isso ao `@lp-backend-dev` como primeiro passo, e o intervalo de polling
se decide depois, com dado real.

**6. Trocar por "Processando arquivo" + barra indeterminada, sem mudar contrato?**
**Sim — e recomendo fazer isso já, independente da decisão sobre o resto.** É custo zero de
backend, resolve a mentira visual imediata ("100% mas ainda processando"), e não compete com
nenhuma decisão de arquitetura pendente. Façam isso nesta semana.

## Sobre as opções A/B/C originais

- **A (SSE/WebSocket): rejeição do front está correta e eu concordo.** Confirmei lendo o BFF
  (`LayoutParserReact/server/dist/src/app.js:230-235`): o proxy Fastify está registrado com
  `websocket: false` explicitamente. Não é omissão, é configuração deliberada — abrir isso é uma
  mudança de superfície na fronteira BFF↔API que precisa passar por decisão de segurança, não só
  de front-end. Não vale o ganho para um processo de poucos segundos.
- **B como desenhada (endpoint novo `/parse/{jobId}/status` com shape próprio): rejeito o shape
  novo, mas endosso a ideia — só que via extensão dos pathways que já existem, não um terceiro.**
- **C (job mínimo: processando/pronto/erro): é essencialmente o que os pathways existentes já
  fazem.** Se formos separar o parse do upload síncrono, C é o nível de granularidade certo — não
  vale a pena para o parse (que não parece ser o gargalo) o mesmo esforço que a transformação já
  tem.

## Se isto virar prioridade: escopo por camada (não por prazo)

Dado que a origem real da demora é a **transformação**, não o parse, e que ela **já** tem ticket
consultável, a ação de menor risco é: **o front passar a consultar
`GET /api/parse/transformations/{ticket}` quando `transformationsStatus === "processing"`**, em
vez de o backend inventar mecanismo novo. Isso não tem escopo de backend nenhum — é mudança
100% de front-end sobre contrato que já existe.

Se, depois de medir (pergunta 5), o **parse em si** também se mostrar lento o suficiente para
justificar granularidade própria, o escopo de backend seria:

- **Controllers/ParseController.cs** — extrair o corpo de parse do `Upload` para permitir retorno
  antecipado com ticket, seguindo a mesma forma dos pathways existentes (200/202 + ticket).
- **Services de armazenamento de ticket** — reaproveitar `LowCodeTransformationStore` (ou um
  primo dele) em vez de criar um terceiro `Store` do zero; risco de duplicar a lição já paga em
  `AiCandidateStore` (TTL, race condition em disco corrigida nesta sessão).
- **Nenhuma mudança em `Program.cs`/DI nova** além do que já existe — os serviços de store já
  estão registrados.

Quem executaria: `@lp-backend-dev` (Dex). Não vejo necessidade de `@lp-parser-llm` aqui — é
puramente camada de orquestração HTTP, não domínio de parsing/IA.

## Resumo para o front

1. Troquem o rótulo/barra por "Processando arquivo" indeterminado **já**, sem esperar decisão de
   contrato — pergunta 6, sim.
2. Passem a consultar `GET /api/parse/transformations/{ticket}` quando
   `transformationsStatus === "processing"` — o mecanismo de vocês pedirem já existe, só não está
   sendo lido pelo front hoje.
3. Não vamos criar `/api/parse/{jobId}/status`. Se o parse (não a transformação) se mostrar lento
   o bastante depois de medido, estendemos o padrão de ticket existente — não inventamos um novo.
4. SSE/WebSocket fica descartado — concordamos com a rejeição de vocês, com uma evidência a mais:
   o proxy do BFF está com `websocket: false` explícito, não por omissão.
