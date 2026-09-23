---
name: board-sync-2026-08-28
description: 5 issues fechadas após auditoria de @lp-architect achar PRs mergeados sem closing keyword efetivo (#140,#138,#139,#141 no LayoutParserApi; #86 no LayoutParserReact).
metadata:
  type: project
---

Fechei via `gh issue close --comment` citando o PR de evidência:
- #140 (LayoutParserApi) — resolvida por PR #205 (motor de resolução estrutural TXT→XML NF-e).
- #138 (LayoutParserApi) — resolvida por PR #203 (sectionMappings Fase 0).
- #139 (LayoutParserApi) — resolvida por PR #201 (RealMapperParser canônico).
- #141 (LayoutParserApi) — resolvida por PR #207 (fieldMappings em execute-candidates).
- #86 (LayoutParserReact) — resolvida por PR #200, mas **cross-repo**: o PR está no LayoutParserApi
  e a issue no LayoutParserReact. `Closes #86` no corpo do PR só fecha automaticamente issues do
  mesmo repositório — por isso ficou OPEN mesmo com o PR mergeado.

**Why:** confirma e generaliza [[project_board-sync-2026-08-18]] — não é só "Closes #N" que às vezes
falha por causa de squash/rebase; closing keywords **nunca** atravessam repositório no GitHub, então
qualquer PR em repo A que resolve issue de repo B precisa de fechamento manual sempre.

**How to apply:** ao revisar PRs candidatos a fechar issues automaticamente, verificar se PR e issue
estão no mesmo repositório antes de assumir que `Closes #N` bastou. Se forem repos diferentes
(comum aqui: LayoutParserApi resolvendo bug relatado no LayoutParserReact), tratar como fechamento
manual obrigatório — não é exceção, é a regra pra esse padrão de referência cruzada na org LayoutParser.
