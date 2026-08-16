---
name: pr-123-ja-mergeada-hardening-secrets
description: PR do hook pre-commit anti-segredo (branch chore/security-hardening-secrets) já foi criada, revisada e mergeada em develop antes desta sessão
metadata:
  type: project
---

Em 2026-08-15, ao receber a missão de publicar `chore/security-hardening-secrets` (commit
`1ecbf69`, hook `.githooks/pre-commit` + `.gitleaks.toml` + confirmação de que a connection
string do SQL nunca é logada), descobri que o trabalho **já estava publicado**: PR #123
(mesmo título) foi mergeada em `develop` às 2026-08-15T23:22:30Z — antes de eu tentar criar
uma nova. `gh pr create` falhou com "No commits between develop and
chore/security-hardening-secrets" porque `git merge-base` já apontava pro tip da branch.

**Why:** provavelmente outra sessão/instância do `@lp-devops` já processou o mesmo handoff
mais cedo (há também PR #124 `chore/security-hardening-ci`, relacionada, tratando rotação da
senha SQL org-wide via runbook/DPAPI).

**How to apply:** antes de `git push`/`gh pr create` numa branch pronta que chega via handoff,
sempre rodar `git merge-base <branch> origin/<base>` (ou `gh pr list --head <branch> --state
all`) primeiro — se o merge-base já é o tip da branch, o trabalho já foi integrado; não tentar
recriar a PR, só reportar o link existente. Ver também [[git-history-purge-2026-08-15]] pro
contexto mais amplo da sessão de hardening desse dia.
