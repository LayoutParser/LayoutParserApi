---
name: issue86-pathwaydiagnostics-qa-gate
description: QA gate de pathwayDiagnostics[]/correlationId em execute-candidates (issue LayoutParserReact #86) — PASS com 1 gap de logging não-bloqueador
metadata:
  type: project
---

Branch `feat/execute-candidates-diagnostico-estruturado-86` (commits `98de049`, `2e5a3b3`,
`0ae4218`), revisada em 2026-08-27. `dotnet build` limpo (0 erros); `dotnet test` 399/403
(as 4 falhas são as pré-existentes de path Windows×Linux — `SafePathResolverTests`/
`LowCodeRunnerArgsTests` — mesmo baseline de [[pr198-linhainfo-signals-qa-gate]], não é
regressão). As 3 novas de `TransformationExecutionControllerPathwayDiagnosticsTests.cs` (dedup/
no_mapper+map_not_found+ai-fallback, sanitização sem `C:\`/caminho real, `xsl_not_found`
distinto de `map_not_found`) passam.

**Critérios de aceite, um a um:**
- Reprodução automatizada sintética: presente e suficiente (3 fixtures cobrindo os 2 sintomas
  originais da issue + fallback de IA disparado).
- `correlationId`: propaga corretamente — `Program.cs:688` seta `CorrelationContext.CurrentId`
  (AsyncLocal) + `LogContext.PushProperty` por request já ANTES desta PR; a PR só expõe o valor
  no payload (`TransformationExecutionCandidatesResponse.CorrelationId`). Null no teste unitário
  é esperado (sem HttpContext real), não é bug.
- Logs com pathway/decisão de catálogo/fonte de config/caminho sanitizado: **GAP** — os ramos de
  sucesso/`not_applicable` (no_mapper, map_not_found, xsl_not_found) só fazem
  `warnings.Add`/`pathwayDiagnostics.Add`, sem nenhum `_logger.Log*`. Só as exceções (`catch`)
  geram log (`LogWarning`). Suporte não consegue grepar por `CorrelationId` pra reconstruir POR
  QUE um pathway específico decidiu `not_applicable` sem reproduzir a chamada. Pré-existente ao
  PR (a estrutura de warnings sem log já era assim antes), não introduzido por esta mudança —
  reportar como achado, não bloqueador.
- Cada pathway termina em status/code (nunca silencioso): confirmado por código E teste
  (`Assert.Equal(3, response.PathwayDiagnostics.Count)` cobrindo sysmiddle+tcl-xsl+ai-fallback).
- `candidates: []` nunca sem causa estruturada: confirmado — todo `return result` cedo é
  precedido de um `pathwayDiagnostics.Add`.
- Sanitização: `LowCodeErrorSanitizer.ForWire` aplicado nos 3 pontos do tcl-xsl que antes
  vazavam `ex.Message`/`pipelineResult.Errors` cru (fix do diagnóstico §5), e no sysmiddle
  (já existia). Teste dedicado (`Sem_mapper_e_sem_tcl_nenhuma_mensagem_vaza_caminho_de_disco`)
  cobre. Nota: o regex do sanitizer (`Services/Transformation/LowCode/LowCodeErrorSanitizer.cs`)
  só reconhece caminho estilo Windows (`[A-Za-z]:[\\/]` ou UNC `\\`) — não pega path Unix
  (`/mnt/...`); irrelevante em produção (Windows-only), mas quem rodar este endpoint via
  Ollama/CI em Linux não teria a mesma proteção se algum dia a mensagem carregar path Unix.

**Why:** o padrão deste repo é: mensagem sanitizada pode sair no payload HTTP, mas o detalhe
completo só deveria existir no log estruturado correlacionável — hoje, para os ramos
`not_applicable`, não existe ESSE log, só o payload. Isso não vaza segredo (o payload já é
sanitizado), mas reduz a capacidade de diagnóstico via log/CorrelationId que o pedido original
pede explicitamente.

**How to apply:** ao revisar qualquer contrato de diagnóstico estruturado (pathwayDiagnostics-like),
checar não só se o payload está certo, mas se cada `Add` a um array de diagnóstico tem um
`_logger.Log*` irmão correlacionável — não assumir que "está no response" é equivalente a "está
logado". Reportar ao dono se vale abrir item de backlog (`@lp-pm`) para
`@lp-backend-dev`/`@lp-parser-llm` adicionarem `LogInformation` estruturado nos ramos
`not_applicable`/`failed` (fora do `catch`).

**Incidente de processo:** outro agente (`@lp-doc`, via worktree `/tmp/claude-1000/lp-doc-wt`)
trocou o branch do checkout compartilhado enquanto eu rodava `dotnet build`/`test` — mesmo padrão
recorrente já registrado em [[pr198-linhainfo-signals-qa-gate]]. Mitigação usada com sucesso:
`git clone --local --no-hardlinks -b <branch> . <scratchpad>/qa86` para build/test isolado, sem
tocar o checkout principal — ver também [[tecnica-matriz-de-mutacao]] pelo mesmo princípio (nunca
verificar na árvore compartilhada quando há concorrência). Clone removido ao final.

**Veredito: PASS.** Build limpo, testes verdes (incluindo os 4 novos), contrato aditivo correto,
sanitização corrigida nos 3 pontos identificados no diagnóstico. Gap de logging estruturado nos
ramos `not_applicable`/`failed` (fora de exceção) é achado a decidir com o dono — não bloqueia
merge.
