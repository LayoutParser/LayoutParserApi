---
name: sectionmappings-readme-doc-issue138
description: Onde e como o README documenta sectionMappings/xmlNamespaces (Fase 0, issue #138/#126) — para retomar/expandir quando #140/#141 avançarem
metadata:
  type: project
---

Documentei em README.md (seção "Rastreabilidade TXT↔XML por linha/seção — Fase 0", logo após a
seção de `pathwayDiagnostics` da issue #86, antes de "## 8. Configuração") os campos aditivos
`sectionMappings`/`xmlNamespaces` de `POST /api/transformationexecution/execute-candidates`.
Commit `48519ed` na branch `feat/section-mappings-fase0-138` (worktree isolado, não no working
tree principal — havia outra branch ativa lá, `feat/resolucao-estrutural-txt-xml-140`).

**Why:** granularidade é LINHA/SEÇÃO, não CAMPO — documentei explicitamente a distinção e que
`sectionMappings` sozinho NÃO desbloqueia a PBI LayoutParserReact #128 (highlight de campo, que
depende de #140/#141). Semântica `null` (pathway não suporta, hoje `tcl-xsl`) vs `[]` (suporta,
nada encontrado) vs preenchido (com `confidence: authoritative`, nunca `best-effort` por
aproximação) ficou em tabela bilíngue.

**How to apply:** ao documentar #140/#141 (rastreabilidade de campo), atualizar esta mesma seção
do README em vez de criar uma nova — e revisar a frase "não desbloqueia #128 sozinha", que deixa
de ser verdade quando #140/#141 forem implementadas. Não linkei nenhum doc de design em
`docs/architecture/` porque, nesta branch/worktree, não existe um doc de design para #138 (o que
existe é `design-resolucao-estrutural-txt-xml-issue-140.md`, mas isso pertence à branch #140,
não a esta). Conferir se um doc próprio da #138 aparece antes de linkar.
