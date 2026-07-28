---
name: lowcode-timeout-concurrency-sync-delivery
description: Correções pós-QA (2026-07-28) no pathway LowCode-auto — timeout+semáforo no runner externo e entrega síncrona com fallback assíncrono no upload/parse.
metadata:
  type: project
---

**Fato:** o QA gate do multi-candidato (`0e5bb22`, ver [[lowcode-auto-multicandidate]]) confirmou
CONCERNS real: `LowCodeTransformationService.TransformAsync` não tinha timeout nem limite de
concorrência protegendo as até N=4 invocações paralelas do runner low-code (processo externo x86,
host FiatMQ sensível a disputa de licença). O usuário também fechou a decisão da arquiteta (Aria):
entrega das transformações vira **síncrona no response do upload, com timeout, caindo pra
assíncrono se estourar** (antes era 100% fire-and-forget, nunca chegava no response HTTP).

**O que mudou** (arquivos: `LowCodeTransformationService.cs`, `LowCodeAutoTransformationService.cs`,
`LowCodeRunnerOptions.cs`, `ParseController.cs`, `Models/Transformation/LowCodeAutoTransformResult.cs`,
`appsettings.json`):

1. **Timeout do runner (`LowCode:RunnerTimeoutSeconds`, default 15s):** cobre o CICLO DE VIDA
   INTEIRO do processo (leitura de stdout/stderr + exit via `Task.WhenAll`), não só a chamada
   isolada a `WaitForExitAsync()`. Motivo: se só a espera de exit tivesse `CancellationToken`, uma
   leitura de stream travada (processo não fecha os handles) ainda escaparia do timeout —
   `Task.WhenAll` só resolve quando o processo de fato morre/fecha os pipes. Implementação corre
   essa combinação (`Task.WhenAll(stdout, stderr, exit)`) contra um `Task.Delay` simples via
   `Task.WhenAny`; se o delay vence, mata o processo (`Kill(entireProcessTree: true)`) e lança
   `TimeoutException`. Validado com harness sintético real (ver abaixo) — deadlock hipotético
   testado e não ocorre.

2. **Limite de concorrência (`LowCode:MaxConcurrentRunners`, default 2):** `SemaphoreSlim` como
   campo de instância de `LowCodeTransformationService`, que é **Singleton** no `Program.cs` — por
   isso o limite vale pro PROCESSO INTEIRO da API (não só dentro de um `Task.WhenAll` de um
   documento). Dois uploads multi-candidato simultâneos ainda respeitam o teto total. Validado com
   harness sintético: cap=2, N=6 processos reais → overlap máximo observado = 2, tempo total ~3
   lotes seriais (não 1 lote paralelo).

3. **Entrega síncrona com fallback (`LowCode:SyncDeliveryTimeoutSeconds`, default 6s):**
   `LowCodeAutoTransformationService` ganhou `RunAsync(...)` (público, retorna
   `Task<LowCodeAutoTransformResult>`) ao lado do `RunInBackgroundAsync` existente (mantido, agora
   delega pro mesmo `TransformAndPersistAsync` privado). `ParseController.Upload` chama `RunAsync`,
   corre `Task.WhenAny(transformTask, Task.Delay(syncTimeout))`: se a transformação vence, inclui
   `transformations` (lista de candidatos, sempre — mesmo N==1 normalizado num array de 1) e marca
   `transformationsStatus="completed"`; se o delay vence, marca `"processing"` e SEGUE processando
   em background (persistência em disco já ocorre dentro do `RunAsync`, então nada se perde) —
   observa exceção via `ContinueWith` pra não gerar unobserved task exception. `"not_applicable"`
   cobre tanto layout fora do escopo (`detectedType != mqseries`) quanto nenhum mapper encontrado no
   banco. `"error"` só em falha ESTRUTURAL (ex.: banco fora do ar ao buscar candidatos) — o
   try/catch em volta de tudo isso no controller garante que o parse principal NUNCA vira 500 por
   causa deste pathway (requisito explícito da tarefa).

**Decisão de escopo deliberada — NÃO mudei a persistência em disco no caso N==1 com falha:** se
`TransformSingleAndPersistAsync` lança exceção (ex.: timeout do runner), nada é persistido em disco
— comportamento idêntico a antes desta mudança. Only a diferença é que agora essa falha também é
OBSERVÁVEL pelo `RunAsync` síncrono (vira um `LowCodeCandidateResult{Success=false}` no array de
retorno), sem alterar o que vai pro disco. Decidi não unificar com o comportamento do caminho
multi-candidato (que persiste candidatos falhos) porque não foi pedido e mudaria o formato dos
artefatos já validados no caminho majoritário (zero-regressão é invariante deste pathway desde a
mudança anterior).

**Valores escolhidos e porquê:**
- `RunnerTimeoutSeconds=15`: bootstrap do runner observado em ~0.5-1s (nota da rodada anterior) →
  15s é folga generosa antes de considerar o host FiatMQ travado.
- `MaxConcurrentRunners=2`: piso do intervalo sugerido (2-3) — bootstrap caro e host sensível a
  concorrência, prefiro o valor mais conservador.
- `SyncDeliveryTimeoutSeconds=6`: bem menor que `RunnerTimeoutSeconds` (candidatos rodam em
  PARALELO via `Task.WhenAll` já existente, não em série — por isso não precisa ser
  `RunnerTimeoutSeconds × N`). 6s cobre o caso comum (runner saudável, poucas variantes) sem
  bloquear o parse além disso.

**Teste sintético rodado (sem SQL/runner real disponível nesta máquina — mesma limitação de
sempre):** harness standalone em `scratchpad/synthtest/` (não versionado, não faz parte do repo)
replicando o MESMO padrão de código (semáforo + `Task.WhenAny` contra processo externo real via
`cmd.exe /c ping` como sleeper, sem mockar nada da lógica de concorrência/timeout em si). Dois
testes, ambos PASS:
- Teste A: processo que dorme 8s com timeout=2s → matado em ~2.2s (não esperou os 8s).
- Teste B: 6 processos de ~2s cada com cap=2 → overlap máximo real observado = 2, tempo total ~6.5s
  (3 lotes seriais, confirma que o semáforo de fato serializa e não é só decorativo).
Não testado: integração fim-a-fim via `dotnet test`/dotnet run da API completa (sem SQL/mapper real
nesta máquina — mesma lacuna reportada na rodada anterior do multi-candidato).

**Pendências para o Quinn (QA) re-testar:**
- Confirmar que `transformationsStatus` nunca é omitido/nulo no contrato de resposta do
  `/api/Parse/upload` (front-end pode passar a depender disso).
- Validar em ambiente com o runner real: timeout de 15s é generoso o suficiente sem ser
  desnecessariamente longo (ajustar depois de medir tempo real de execução end-to-end, não só
  bootstrap).
- Confirmar que a mudança de `TransformSingleAndPersistAsync`/`TransformMultiCandidateAndPersistAsync`
  para retornar valor (em vez de `Task` void) não quebrou nenhum outro chamador — grep feito nesta
  sessão não achou outros consumidores além de `TransformAndPersistAsync`, mas vale conferir de novo
  se algo novo foi adicionado depois.
- `TransformationExecutionController.ExecuteLowCode` (pathway manual, chama `LowCodeTransformationService.TransformAsync`
  direto) agora também herda o timeout+semáforo — não testado manualmente por falta de runner real,
  mas é o mesmo código, então o comportamento deve ser o mesmo dos testes sintéticos.
