---
name: lowcode-auto-multicandidate-qa-gate
description: QA gate 2026-07-28 do commit 0e5bb22 (multi-candidato LowCode-auto) — dedup/paralelismo corretos (validado sintético), mas SEM timeout/proteção de concorrência no runner externo (CONCERNS confirmado, não FAIL só porque N==1 majoritário não regride). RE-VALIDADO E FECHADO (PASS) com o fix bd8279c: timeout+semáforo no runner e entrega síncrona com fallback no ParseController, confirmados por leitura de código + harness isolado próprio (não só o relatório da Lia).
metadata:
  type: project
---

**RE-GATE 2026-07-28 (PASS) — fix `bd8279c` fecha a pendência:** revisei linha a linha (não só
confiei no relatório da Lia/Dex):
1. `LowCodeTransformationService.TransformAsync`: `SemaphoreSlim` de instância (Singleton no
   `Program.cs`, confirmado registro `AddSingleton<LowCodeTransformationService>` +
   `Configure<LowCodeRunnerOptions>(section "LowCode")` — resolve via DI sem problema) libera
   sempre via `finally`, mesmo no caminho de timeout (o `throw new TimeoutException` acontece
   DENTRO do `try`, então o `finally { Release(); }` roda de qualquer forma — sem deadlock/leak).
   Timeout cobre `Task.WhenAll(stdout, stderr, exit)` contra `Task.Delay`, não só `WaitForExitAsync`
   isolado — fecha exatamente o gap que a Lia descreveu.
2. `ParseController.Upload`: tracei o `Task.WhenAny(transformTask, Task.Delay(syncTimeout))` e
   TAMBÉM escrevi um harness isolado próprio (fora do repo, scratchpad) reproduzindo só esse padrão
   genérico (sem SQL/runner) com Tasks sintéticas — 4 cenários, todos PASS: (a) task lenta (5s) +
   timeout 1s → cai pra "processing" em ~1s, nunca espera os 5s; (b) task rápida (300ms) + timeout
   2s → completa em ~300ms, não espera o teto; (c) task falha rápido (200ms) → cai no catch externo,
   status="error"; (d) task falha DEPOIS do timeout → "processing" imediato, exceção de fundo
   observada depois via `ContinueWith` sem crash/unobserved exception. Isso fecha especificamente o
   gap que a própria Lia apontou como não testado ("sem SQL/mapper real nesta máquina").
3. Grep repo inteiro por `RunInBackgroundAsync`/`RunAsync`/`TransformSingleAndPersistAsync`/
   `TransformMultiCandidateAndPersistAsync`: único consumidor externo é `ParseController`
   (via `RunAsync`); `TransformationExecutionController.ExecuteLowCode` chama
   `LowCodeTransformationService.TransformAsync` direto — assinatura pública dele NÃO mudou
   (`Task<string>`), só ganhou semáforo+timeout internos, transparente pro chamador. `dotnet build`
   limpo (0 warnings/erros) confirma que nada quebrou.
4. `transformationsStatus` no JSON de `/api/Parse/upload`: confirmado por leitura completa do
   método que os 4 estados (`completed`/`not_applicable`/`processing`/`error`) cobrem 100% dos
   caminhos que chegam ao único `return Ok(...)` da rota principal (variável inicializada em
   `"not_applicable"` antes do try, reatribuída em cada branch). **Achado à parte (não é regressão
   deste commit, é comportamento pré-existente fora do diff revisado):** o branch de retorno
   antecipado quando `isXmlFile`/`detectedType=="xml"` (linhas ~85-96 do controller) tem um shape de
   resposta totalmente diferente e NÃO inclui `transformationsStatus` — se o front-end passar a
   depender desse campo estar sempre presente em qualquer 200 OK do endpoint, esse branch quebra a
   expectativa. Reportar ao Dex como item separado, não bloqueia este gate.
5. Sem runner Sysmiddle real disponível nesta máquina (mesma limitação de sempre) — não inventei
   teste fim-a-fim com runner/SQL reais; os pontos 1-2 acima cobrem a mecânica de concorrência/
   timeout/fallback de forma independente e suficiente para aprovar.

**Considero este escopo fechado — sem CONCERNS pendentes**, exceto o achado à parte do item 4
(pré-existente, fora do diff, reportado como sugestão de follow-up).

**Confirmado correto (PASS):** `LowCodeAutoTransformationService.TransformMultiCandidateAndPersistAsync`
usa try/catch **dentro de cada lambda** antes do `Task.WhenAll` — nenhuma Task fica "faulted";
toda falha de candidato vira um `LowCodeCandidateResult{Success=false}` normal. Reproduzi com
harness sintético (4 candidatos, #2 lança exceção): os 4 completam, 1 isolado como falha, tempo
~paralelo (não 4x serial). `GetRankedMapperCandidatesForLayoutGuidAsync` tem exatamente a mesma
ordem de prioridade de `GetBestMapperForLayoutGuidAsync` (input match > target match > mais
recente) — dedup por `MapperGuid` mantendo a primeira ocorrência = `ranked[0]` sempre bate com o
que `GetBestMapperForLayoutGuidAsync` escolheria. Caminho `N==1` chama
`TransformSingleAndPersistAsync`, que é o MESMO código (só extraído pro próprio método, comparado
via `git show 0e5bb22` linha a linha) — zero regressão no caminho majoritário confirmada por diff,
não só por inspeção.

**Gap confirmado (CONCERNS — reportar, não é motivo de FAIL sozinho):** nenhum timeout/proteção de
concorrência protege as até N=4 invocações paralelas do runner low-code
(`LowCodeTransformationService.TransformAsync` → `Process.Start` + `p.WaitForExitAsync()` sem
`CancellationToken`/timeout). Confirmado por grep: zero uso de `SemaphoreSlim` em todo o projeto
(fora de XML doc de um pacote NuGet, falso positivo). Cada invocação usa nomes de arquivo únicos
(`Guid.NewGuid()`) então não há colisão de arquivo entre os 4 candidatos nesse nível — mas nada
impede 4 processos do runner rodando ao mesmo tempo (bootstrap ~0.5-1s cada, segundo a Lia) de
disputar recurso interno do host FiatMQ (init de licença já documentada como sensível — ver memória
do usuário `sysmiddle-runtime-e-sintese`). Se o runner travar, o request HTTP que disparou o
fire-and-forget já retornou (é background), mas o próprio background fica preso indefinidamente
sem timeout — não derruba o usuário, mas pode acumular processos travados/zumbis ao longo do tempo
sem qualquer circuit breaker.

**Why:** era exatamente a preocupação original da Aria (arquiteta) no pedido desta rodada de QA —
"existe ALGUM timeout/cap protegendo N candidatos". Resposta factual: não. Isso é addressable sem
redesenhar (ex.: `CancellationTokenSource` com timeout por candidato + `SemaphoreSlim` limitando
invocações simultâneas do runner), mas não foi implementado nesta mudança e não estava no escopo
declarado pelo Dex/Lia (`lowcode-auto-multicandidate.md` do lp-parser-llm não menciona isso).

**How to apply:** ao revisar qualquer expansão futura deste pathway (N maior, ou uso do array de
candidatos por um consumidor real), cobrar explicitamente timeout por candidato + limite de
concorrência do runner antes de aprovar — o padrão atual (paralelismo irrestrito de processo
externo) escala mal e sem tremer nenhum alarme se travar.
