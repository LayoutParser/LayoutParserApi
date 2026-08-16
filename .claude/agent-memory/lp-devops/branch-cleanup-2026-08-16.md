---
name: branch-cleanup-2026-08-16
description: Limpeza geral de branches remotas 2026-08-16 — só develop/master ficaram, exceto uma achada com trabalho real não mergeado
metadata:
  type: project
---

Limpeza pedida pelo dono em 2026-08-16: commitar pendências e deletar todas as branches remotas
exceto `develop`/`master`. Usei `git cherry origin/develop <branch>` (compara patch-id, não hash)
porque o histórico foi reescrito em 2026-08-15 (`git filter-repo`) e comparação por hash daria
falso positivo de "não mergeado".

**Resultado:** 41 branches remotas deletadas (todas as `fix/*`, `feat/*`, `docs/*`, `chore/*` já
mergeadas em develop, e ~30 `worktree-agent-*`/`worktree-wf_*` órfãs). Ficaram só `develop`,
`master`, e 2 branches do dependabot (não mexidas por instrução explícita).

**Achado importante — `worktree-agent-a1403e675beb9d14f` NÃO foi deletada:** tem um commit
(`e1edfd4`, "senha SQL não pode ser rotacionada - credencial org-wide") com conteúdo real que
não está no `.claude/rules/security.md` atual: a senha SQL é compartilhada por ~231.890 times na
NDD inteira, então rotação foi descartada como opção (diferente do texto atual do arquivo, que
ainda diz "rotacionar, escalado ao DBA"). Isso muda a prioridade de mitigação pra "limpeza de
histórico é a única ação real" + hardening em repouso + hook anti-reincidência. Ver [[git-history-purge-2026-08-15]]
e [[gemini-decommission-secrets]] pro resto do histórico de segredos.

**Why:** decisão real do dono sobre uma credencial compartilhada, capturada numa branch de
worktree que nunca virou PR — perder isso significaria a doc de segurança ficar desatualizada
sobre um ponto crítico (rotação da senha SQL não é mais uma opção viável).

**How to apply:** antes de continuar qualquer trabalho em `security.md` ou no plano de rotação de
segredos, mesclar o conteúdo de `e1edfd4` (branch `worktree-agent-a1403e675beb9d14f`, ainda no
remoto) no arquivo atual, então só depois considerar deletar a branch. Não deletar essa branch
sem essa reconciliação.
