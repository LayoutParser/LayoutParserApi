---
name: sessoes-concorrentes-commit-por-item
description: Neste repo é rotina haver outras sessões de Claude Code editando a mesma árvore. Commitar por item (não acumular) e sempre `git add` com caminho explícito — nunca `git add -A`/`commit -a`.
metadata:
  type: feedback
---

Commite **assim que cada item fechar**, em vez de acumular tudo para o fim, e faça `git add` sempre
com **caminho explícito** dos arquivos que você editou. Nunca `git add -A`, `git add .` ou
`git commit -a`.

**Why:** é rotina neste projeto haver outras sessões de Claude Code trabalhando na MESMA árvore ao
mesmo tempo (em 2026-07-31 havia duas: uma no `LayoutParserReact` e outra neste repo, no Handoff 3,
que ia tocar o mesmo `AiMetricsController.cs`). Colisão já aconteceu 2x numa única semana. Além
disso, a árvore quase sempre tem arquivos que **não são seus** e não devem ser comitados por
acidente: `.claude/agent-memory/*` de outros agentes, `.claude/worktrees/`, `.codex/`, `AGENTS.md`,
handoffs recém-escritos pela arquiteta.

**How to apply:** rode `git status --short` no início e reconheça o que já estava sujo antes de
você. Ao fechar cada item da missão, `git add <caminhos> && git commit`. Se dois itens tocam o mesmo
arquivo compartilhado (típico: um controller), agrupe-os para fechar aquele arquivo mais cedo e
reduzir a janela de colisão — mesmo que a ordem de prioridade da missão sugira outra sequência.
