---
name: audit-doc-2026-08-28-issues-desalinhadas
description: Auditoria de documentação/backlog em 2026-08-28 — issues #86/#138/#139/#140/#141 mergeadas mas OPEN, README com secao stale/contraditoria
metadata:
  type: project
---

Auditoria completa (branch `feat/resolucao-estrutural-txt-xml-140`, que na verdade já foi
mergeada via PR #205 — o branch local ficou "sobrando" com artefatos de sessão não commitados).

**Achado #1 — issues fechadas por PR mas não fechadas no board:**
- #140 (motor resolução estrutural) → PR #205 MERGED, issue ainda OPEN.
- #138 (sectionMappings Fase 0) → PR #203 MERGED, issue ainda OPEN.
- #139 (RealMapperParser canônico) → PR #201 MERGED, issue ainda OPEN.
- #141 (fieldMappings execute-candidates) → PR #207 MERGED, issue ainda OPEN.
- LayoutParserReact#86 (diagnóstico estruturado) → PR #200 (neste repo) diz "fecha
  LayoutParserReact#86" no título, mas closing keywords **não atravessam repositório** — a
  issue no repo React continua OPEN. Precisa fechamento manual.
Causa provável: nenhuma das PRs usou `Closes #N` no corpo (ou usou mas o número não bateu
com o repo certo). Ação: `@lp-pm` fecha as 5 issues citando o PR/commit que resolveu.

**Achado #2 — README com seção stale contradizendo seção nova (mesmo arquivo):**
Em `README.md` (branch `develop`), a linha ~235 (§5, visão de IA) ainda diz
"`/fieldMappings` ainda sem consumidor HTTP, escopo futuro issues #140/#141" e o Roadmap
(§14) tem esses itens como `[ ]` não feitos — enquanto a seção ~371-500 do mesmo arquivo já
documenta `fieldMappings`/`sectionMappings` como implementados e testados (issue #141
mergeada). Contradição dentro do mesmo doc: alguém atualizou a seção de contrato mas
esqueceu de voltar no resumo executivo/roadmap. Ação: `@lp-doc` reconcilia §5 e §14 com a
seção detalhada, sem duplicar conteúdo.

**Achado #3 — branch local órfã com artefatos de sessão não commitados:**
`feat/resolucao-estrutural-txt-xml-140` tinha 5 memórias de agente + 2 design docs
(#86, #140) nunca commitados, mesmo com o código já mergeado via PR #205. Commitei
localmente nesta sessão (`27b49f8`) — falta `@lp-devops` decidir se faz push dessa branch
órfã (provavelmente não vale a pena, o conteúdo relevante devia ir direto pra `develop` via
um PR só de docs) ou se cria um PR "docs only" a partir daqui.

**Padrão a vigiar:** pelo menos 3 dessas PRs (#198, #200, #205) tiveram commits de "chore:
security baseline SCS0018" — indica que o baseline de segurança precisa de atualização
manual toda vez que código novo desloca linhas, ver se vale automatizar (fora do escopo
desta auditoria, mas repetiu 3x).
