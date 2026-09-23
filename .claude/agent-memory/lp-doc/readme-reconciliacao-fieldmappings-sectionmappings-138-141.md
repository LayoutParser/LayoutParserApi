---
name: readme-reconciliacao-fieldmappings-sectionmappings-138-141
description: Reconciliação da contradição §5/Roadmap vs §7 no README sobre fieldMappings/sectionMappings (issues #138-141) — commit dc4ef79
metadata:
  type: project
---

A working tree local (branch `feat/resolucao-estrutural-txt-xml-140`) estava **muito atrás**
de `origin/develop`: não tinha localmente os merges das PRs #201 (#139), #203 (#138), #205 (#140)
e #207 (#141), mesmo essas PRs já estando MERGED no GitHub. `git branch`/`git log` locais não
refletiam isso — foi preciso `git fetch origin` e comparar com `origin/develop` para achar o
estado real. [[concorrencia-git-worktree-isolado]] segue relevante: antes de assumir que algo
"não está implementado" nesta sessão, sempre `git fetch` + comparar com `origin/<branch-alvo>`,
não só `git log` local.

**O que foi feito:** copiei o README.md de `origin/develop` (que já tinha as seções §7
`fieldMappings em execute-candidates` e `Rastreabilidade TXT↔XML por linha/seção` documentadas
como implementadas) para a working tree, e por cima apliquei a correção que a Aria (lp-architect)
apontou: §5 ("A visão de IA", linhas ~235/242) e o Roadmap (§14) ainda diziam "sem consumidor
HTTP" / "issues #140/#141 pendentes", contradizendo o §7 que já documentava os campos como
prontos. Também corrigi duas referências cruzadas obsoletas: a seção de `fieldMappings` (#141)
dizia "sectionMappings em documentação — pendência conhecida" (já não era mais pendência, a seção
já existia logo abaixo) e a seção de `sectionMappings` (#138) dizia "rastreabilidade campo-a-campo
é escopo de #140/#141, ainda não implementado" (já estava implementado na seção acima).

**Preservado (não é contradição, é ressalva real):** o aviso de que a validação comportamental
de `fieldMappings` contra o `LowCodeRunner.exe` real (Windows-only, não roda em WSL/Linux) não foi
feita — só existe validação estrutural sintética (20 fixtures). Não removi essa ressalva; ela
continua válida mesmo com o contrato "implementado".

**Commit:** `dc4ef79` em `feat/resolucao-estrutural-txt-xml-140`, só `README.md`. Não fiz push.

**How to apply:** se aparecer nova tarefa de reconciliar README, primeiro `git fetch origin` e
`git diff <branch-local> origin/develop -- README.md` para achar drift antes de editar manualmente.
