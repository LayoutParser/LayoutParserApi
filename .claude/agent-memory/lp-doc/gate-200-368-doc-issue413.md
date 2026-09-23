---
name: gate-200-368-doc-issue413
description: Onde/como o fechamento doc-only do gate React#200/epic #368 (issue #413) foi publicado, e a divergência de 401 achada no processo
metadata:
  type: project
---

Issue #413 (2026-09-15): documentei os 6 itens do gate React#200/epic #368 já implementados na
API, em doc novo
[`docs/architecture/contratos-gate-200-368-2026-09-15.md`](../../docs/architecture/contratos-gate-200-368-2026-09-15.md),
linkado do README §8.2. Fontes de verdade usadas:
[`cross-check-gate-200-368-2026-09-15.md`](../../docs/architecture/cross-check-gate-200-368-2026-09-15.md)
(análise do @lp-architect) e o doc anterior de #376,
[`contrato-rbac-erro-diff-mapping-fiscal-2026-09-10.md`](../../docs/architecture/contrato-rbac-erro-diff-mapping-fiscal-2026-09-10.md).

**Divergência encontrada (sinalizada no doc, não corrigida silenciosamente):** o doc de #376
afirma "não existe 401 na API" como regra geral (identidade vem do BFF, ausência responde 404
fail-closed). Isso é verdade para os endpoints de mapping fiscal, mas `GET /api/workspaces/me`
(`WorkspacesController.cs:41-42`) **devolve 401 explícito** por design deliberado (comentário no
código: "primeiro endpoint em que 'anônimo responde algo' deixa de fazer sentido"). Documentei
isso como nota de divergência no novo doc — não é bug, é exceção intencional ao padrão geral.

**Item 6 (capability Sysmiddle) não fecha:** enforcement existe (`MappingEngineGuardFilter`,
issue #232, fechada), mas o payload consultável `GET .../capabilities` pedido no checklist
original do #232 não existe — isso é a issue #415, documentado como gap real, não como resolvido.

**Correção de processo cometida e corrigida nesta sessão:** commitei o doc direto em `develop`
antes de criar a branch de feature pedida (`docs/gate-200-contratos-fechados-413`). Corrigido com
`git branch <nome> && git reset --hard <commit-anterior> && git checkout <nome>` — sempre criar/
checkout a branch ANTES do primeiro commit quando a tarefa especificar uma branch nova.
[[concorrencia-git-worktree-isolado]]

Swagger **já inclui XML docs** (`Program.cs:309`, `IncludeXmlComments`) — atualiza uma memória
antiga ([[fieldmappings-execute-candidates-141-doc]]) que dizia o contrário; isso mudou em algum
commit entre 09-10 e agora, não verificado quando exatamente.
