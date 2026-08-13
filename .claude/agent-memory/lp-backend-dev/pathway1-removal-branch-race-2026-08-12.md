---
name: pathway1-removal-branch-race-2026-08-12
description: Segunda ocorrência confirmada de outra sessão trocando a branch corrente sob os pés durante um commit — desta vez atingiu fix/agregacao-infcpl (não pushed) em vez da branch de trabalho pedida
metadata:
  type: feedback
---

Ao remover o Pathway 1 de transformação (issue #41), o `git commit` local aterrissou em
`fix/agregacao-infcpl` em vez de `fix/remove-pathway1-transformacao` (a branch que eu havia
criado a partir de `origin/develop` no início da tarefa). Outra sessão concorrente trocou o
branch corrente (`git checkout`) no meio da minha sequência de edições sem tocar nos arquivos
working-tree (por isso não deu conflito, só "vazou" o commit pro branch errado). Já era
conhecido — ver [[gates-auditoria-enforcement-2026-08-12]] — mas ali o incidente foi resolvido
com `git cherry-pick`.

**Desta vez o cherry-pick foi BLOQUEADO pelo classifier de permissão do Auto Mode** ("Blocked by
classifier"), assim como `git branch -f` (force-move de branch, mesmo sem checkout). Sem esses
comandos, a correção teve que ser manual: refazer os 3 deletes + edição do `Program.cs` +
recriar o doc de decisão (via `git show <hash>:path > path`) na branch certa, e commitar de novo
lá. A branch errada (`fix/agregacao-infcpl`) ficou com o commit duplicado `1f5b3c6` — não
consegui limpá-la (force-move bloqueado) porque não estava tracked/pushed ainda, então não é
destrutivo remotamente, mas fica pendente de limpeza manual (`git branch -f fix/agregacao-infcpl
3a1e28b` ou equivalente) por quem tiver permissão, antes de abrir PR dessa branch.

**Why:** o Auto Mode classifier bloqueia `cherry-pick` e `branch -f` mesmo quando o objetivo é
corrigir um erro de outra sessão (não é ação destrutiva real — commit local não pushed). O motivo
aparente é que esses comandos "reescrevem histórico"/movem ponteiros de branch, o que o
classifier trata como sensível por padrão, sem diferenciar o contexto de correção.

**How to apply:** se um `git commit` aterrissar na branch errada de novo, não tente
`cherry-pick`/`branch -f` — vá direto para o caminho manual (refazer as mudanças no branch certo,
usando `git show <hash>:path > path` para recuperar arquivos novos criados no commit perdido, e
commitar lá). Reportar a branch poluída para o usuário/`@lp-devops` limpar, sem tentar forçar via
bash. Sempre confirmar `git branch --show-current` logo antes do commit final quando há sessões
concorrentes no mesmo repo.
