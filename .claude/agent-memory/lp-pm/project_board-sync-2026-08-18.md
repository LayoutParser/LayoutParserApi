---
name: project-board-sync-2026-08-18
description: Board-sync completo de 2026-08-18 — 7 issues fechadas com evidência real (PR mergeada), várias outras revisadas e mantidas abertas por falta de evidência.
metadata:
  type: project
---

Revisão completa do board (missão `board-sync`) pedida pelo dono em 2026-08-18: mover para Done
apenas o que tem evidência real (PR mergeada/código no repo), nunca por suposição.

**Fechadas nesta sessão** (issue closed + Status=Done no Project #2):
- **#122** — PR #160 (`pr-validate.yml` roda dotnet build/test em PR→develop).
- **#33** — código morto confirmado (Gemini/Semantic não referenciados) + `DataGenerationControllerDiTests` real; reforçada pela PR #89.
- **#111 / #113** — PR #115 (itens A2/A1 da auditoria #108: health check Ollama órfão + `ValidateOnStart` do LowCode). QA PASS com teste de mutação.
- **#92 / #93** — PR #105 (`Closes #92, Closes #93` no corpo, mas não fechou automaticamente — branch de merge não era a associada). Particionamento do `AiCandidateStore` por usuário + abertura dos 3 endpoints para qualquer autenticado.
- **#51** — PR #89 seção 2 (TTL real do `AiCandidateStore`). Fechamento anterior da #51 tinha citado fix no lugar errado (`XslSynth/Metrics/RunManifest.cs`); ficou reaberta até esta correção real.

**Lição confirmada:** `Closes #N` no corpo da PR não é garantia de fechamento automático — vale
conferir manualmente se a issue realmente fechou após o merge, principalmente quando a PR não
mergeia direto na branch "home" associada ao board. Ver também [[backlog-nao-e-prova-do-codigo]].

**Revisadas e mantidas abertas (sem evidência de PR/commit que resolva)** — não mexer sem
segunda checagem: #151, #141, #140, #139, #138, #137, #113(fechada acima), #112, #110, #108,
#104, #103, #102, #99, #98, #97, #96, #95, #94, #90, #88. `gh pr list --search "#N"` deu muito
falso positivo (número aparece em diff/texto sem relação com a issue) — não usar como prova
sozinha, sempre ler o corpo da PR encontrada antes de aceitar o match.

#110/#112 (dry-run de config drift em produção / ativar `MIGRATE_CONFIG_TO_REPO`) são ações
operacionais que dependem de rodar deploy.yml contra produção — não há como confirmar via
git/PR, só perguntando ao `@lp-devops` se já rodou.
