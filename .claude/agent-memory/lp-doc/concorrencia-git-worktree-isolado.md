---
name: concorrencia-git-worktree-isolado
description: repo compartilhado por múltiplos agentes concorrentes trocando branch na mesma working tree; usar git worktree isolado para commits seguros
metadata:
  type: feedback
---

O diretório principal do repo (`/mnt/c/Users/elson.lopes/source/repos/LayoutParserApi`) é
compartilhado por vários agentes rodando em paralelo (`.claude/worktrees/agent-*` sugerem que
o harness normalmente isola cada agente em seu próprio worktree, mas nem sempre isso acontece —
nesta sessão o agente estava operando direto na working tree principal). Observado em 2026-08-27:
outro agente fez `git checkout`/`stash` na mesma working tree enquanto eu tentava commitar,
causando `.git/index.lock` recorrente e a branch mudando sob meus pés no meio da operação
(`git stash pop` chegou a aplicar minhas mudanças na branch errada).

**Why:** commitar direto na working tree principal quando há indícios de atividade concorrente
(lock de índice reaparecendo, branch mudando sozinha) arrisca perder trabalho ou poluir a branch
errada.

**How to apply:** se `git status`/`git branch --show-current` mostrar instabilidade (branch
inesperada, lock recorrente), não insistir em checkout na working tree principal. Em vez disso:
1. Salvar o diff das mudanças próprias em um patch (`git diff -- <arquivos> > patch`) — não
   depender só de `git stash`, que pode ser aplicado na branch errada numa corrida.
2. Reverter os arquivos próprios (`git checkout -- <arquivos>`) para não interferir no outro agente.
3. Criar um `git worktree add <tmp-dir> <minha-branch>` isolado, aplicar o patch lá
   (`git apply`), buildar/commitar nesse worktree, depois `git worktree remove --force`.
Isso evita qualquer disputa de índice/HEAD com outros agentes ativos na working tree principal.
