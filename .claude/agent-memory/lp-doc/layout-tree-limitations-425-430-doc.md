---
name: layout-tree-limitations-425-430-doc
description: README §8.3 documenta GET .../layout-tree (issue #425/#430) e a divergência entre layout-tree.rules[] (só vínculo direto) e explanation.rules[] (inclui DSL)
metadata:
  type: project
---

Documentei `GET /api/workspaces/{workspaceId}/mappings/{mappingId}/layout-tree` no README
(§8.3, bilíngue PT/EN), na branch `fix/layout-tree-node-identity-430` (commit `a85a4d3`),
a pedido de handoff após o commit `71fdfa4` (fix da issue #430) sobre o endpoint da issue #425.

**Fato central a preservar:** `layout-tree.rules[]` cobre só vínculo direto campo→campo
(`LinkMappingItemVO`) — regras condicionais/DSL (`MapperRule`/branches, prefixo `I.`/`T.`) só
aparecem em `GET .../explanation` (Slice 4). Isso é decisão deliberada de escopo (não dá pra
resolver GUID de nó a partir de texto DSL sem risco de inventar GUID não-único), documentada em
`Models/Dtos/Fiscal/LayoutTree.cs:29-41` como XML doc do `LayoutTreeResponse`. O campo
`Limitations: IReadOnlyList<string>` (novo na #430) sinaliza isso em runtime quando o mapper tem
regras DSL.

**Why:** o React (consumidor futuro da UI de dupla-árvore) pode facilmente assumir que
`layout-tree.rules[]` é a lista completa de vínculos do mapper e perder as regras DSL — o README
agora tem uma nota ⚠️ explícita nos dois idiomas pra evitar essa confusão.

**How to apply:** se `layout-tree` ou `explanation` mudarem de contrato no futuro, revisar
README §8.3 junto. XML docs em `Controllers/LayoutTreeController.cs` e
`Models/Dtos/Fiscal/LayoutTree.cs` já estavam completos nesta sessão — não precisei alterá-los,
só o README. Relacionado a [[gate-200-368-doc-issue413]] (mesmo padrão bilíngue de contrato de
endpoint) e [[rbac-erro-diff-376-doc]] (mesma seção §8 do README).
