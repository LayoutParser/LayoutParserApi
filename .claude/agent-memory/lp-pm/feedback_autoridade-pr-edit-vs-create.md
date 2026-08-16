---
name: feedback-autoridade-pr-edit-vs-create
description: Editar corpo de PR e comentar em PR estão dentro da autoridade de @lp-pm; criar/mergear PR e push continuam exclusivos de @lp-devops
metadata:
  type: feedback
---

**`gh pr edit --body` e `gh pr comment` são permitidos a `@lp-pm`. `gh pr create`, `gh pr merge` e `git push` continuam bloqueados** (exclusivos de `@lp-devops`).

**Why:** a regra em `.claude/rules/agent-authority.md` lista "`gh pr create`/`gh pr merge`" como exclusivos porque **PR é o portão de release**. Corrigir a *descrição* de uma PR ou comentar nela não abre nem fecha portão nenhum — é higiene de comunicação do backlog, o mesmo trabalho de traduzir sinal em registro rastreável. O dono confirmou isso na prática em 2026-08-15, ao me pedir explicitamente para corrigir o corpo enganoso da PR #89 e postar comentário de correção de rota.

**How to apply:** quando um achado for mal rotulado numa PR já aberta, corrigir a comunicação em vez de mexer no código ou na branch — e preservar o resto do corpo intacto. Procedimento que funcionou e vale repetir: `gh pr view <n> --json body -q .body` para um arquivo, substituição **literal e única** do trecho alvo via script (com `assert` de que reverter a troca reproduz o original byte a byte, provando ausência de dano colateral), `diff` para inspeção, e só então `gh pr edit --body-file`. Nunca reescrever o corpo "de memória" — PRs deste repo têm corpo longo e cheio de detalhe verificado que se perde fácil. Se a correção exigir mexer no commit, na branch ou no merge, **parar e devolver a `@lp-devops`**.

Related: [[project-backlog-nao-e-prova-do-codigo]], [[reference-gh-cli-setup]]
