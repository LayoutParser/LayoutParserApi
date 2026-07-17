---
name: branch-audit-habit
description: Sempre auditar branches de "trabalho em andamento" com git diff completo (não --stat/log) antes de confiar que elas contêm código — neste projeto já aconteceu de uma branch só ter docs
metadata:
  type: feedback
---

Ao avaliar o estado de uma branch de trabalho pendente neste projeto, sempre rodar
`git diff <base>...<branch>` (diff completo, não só `--stat` ou `git log --oneline`) antes de
reportar "fase X está feita/parcial". `--stat` mostra contagem de linhas por arquivo mas não revela
que os arquivos alterados são só `.md`/memory — só o diff de conteúdo (ou pelo menos a lista de
paths) deixa isso inequívoco.

**Why:** em 2026-07-16 encontrei uma branch (`docs/track-a-a1-status`) cujo nome e commits sugeriam
trabalho de código de A1 (varredura em lote do runner), mas o diff completo revelou que ela só
tinha atualizações de documentação/memória — o "código" do modo lote nunca existiu, foi um harness
bash externo ao repo. Ver [[track-a-reconciliation]].

**How to apply:** em qualquer missão `analyze-impact` ou auditoria de estado (branches paralelas,
handoff entre sessões), rodar o diff completo antes de assumir que uma branch tem o código que o
nome/commit message sugere.
