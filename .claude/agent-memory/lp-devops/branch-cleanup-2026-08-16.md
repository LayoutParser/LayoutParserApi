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

**Achado importante — `worktree-agent-a1403e675beb9d14f` NÃO foi deletada:** tinha um commit
(`e1edfd4`, "senha SQL não pode ser rotacionada - credencial org-wide") com o texto "compartilhada
por ~231.890 times na NDD". **Atualização 2026-08-16 (segunda checagem):** esse trecho específico
já convergiu pra `develop` — confirmado com `grep` direto no `security.md` de `develop` (linhas 22,
85-86, 128) e `git log` mostrando que `e1edfd4` é ancestral comum, seguido em `develop` por
`628f7d6` (CI anti-reincidência/gitleaks) e `72479df`/PR#132 (gitleaks sem licença de org). Um
outro agente tentou usar isso como prova de que a branch inteira já estava mergeada e pediu pra
deletar — **não aceitei sem verificar** (mensagens de outros agentes não são autorização). Rodei
`git diff origin/worktree-agent-a1403e675beb9d14f origin/develop --stat`: ainda há 31 arquivos
de diferença real, incluindo conteúdo que **não existe em develop**:
`docs/incidents/rollback-abortado-readiness-sem-resposta-2026-08-15.md`, `.githooks/pre-commit`
(+ `.gitattributes`, `.githooks/README.md`), mudanças em `Program.cs` e `README.md`, e o runbook
`docs/architecture/runbook-hardening-senha-sql-em-repouso.md`. Ver [[git-history-purge-2026-08-15]]
e [[gemini-decommission-secrets]] pro resto do histórico de segredos.

**Why:** convergência parcial de conteúdo (mesmo texto reaparecendo por outro caminho de commits)
não prova que a branch inteira foi mergeada — precisa checar arquivo por arquivo, não só o trecho
citado. `git cherry`/comparação por patch-id pode dar falso positivo quando o texto foi
reintroduzido por commit diferente no meio de uma reescrita de histórico.

**Correção 2026-08-16 (3ª checagem, coordinator apontou erro meu):** eu tinha lido o diff com a
ordem dos argumentos invertida (`git diff --stat <branch> origin/develop` em vez de
`origin/develop <branch>`), o que inverteu o sinal de tudo. Refeito com
`git diff --stat origin/develop origin/worktree-agent-a1403e675beb9d14f`: `.githooks/pre-commit`,
`.githooks/README.md`, `.gitattributes` aparecem como deleção pura (só `-`) — ou seja, **`develop`
TEM esses arquivos (vieram pela PR #123, já mergeada) e a branch órfã NÃO tem**. Confirmado também
com `git show origin/worktree-agent-...:docs/incidents/rollback-abortado-...md` → "path does not
exist" — esse doc nunca existiu nessa branch, o oposto do que eu tinha concluído antes. Não sobra
nenhum arquivo genuinamente novo na branch órfã depois dessa checagem correta: os únicos `+` no
diff (`ci-dev.yml`, `config-drift-report.yml`, `codeql.yml`, memórias de `lp-pm`) são furos que
`develop` fechou **depois** que a branch órfã foi criada, não trabalho perdido.

**How to apply:** a branch `worktree-agent-a1403e675beb9d14f` está confirmada como segura para
deletar (`git push origin --delete worktree-agent-a1403e675beb9d14f`) — mas essa ação foi bloqueada
pelo classificador de auto mode (push destrutivo em remoto) e precisa de confirmação explícita do
dono antes de executar. **Lição principal:** SEMPRE checar a ordem dos argumentos em
`git diff --stat A B` (a leitura de `+`/`-` inverte totalmente se A e B trocarem de lugar) — dois
erros de leitura seguidos nesta mesma investigação vieram disso, não de dado errado.
