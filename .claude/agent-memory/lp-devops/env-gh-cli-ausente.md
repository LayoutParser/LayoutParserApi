---
name: env-gh-cli-ausente
description: gh CLI NÃO está instalado na workstation de dev — status de runs do CI dev sai do log local do runner (_diag/Worker_*.log); PRs só via browser
metadata:
  type: project
---

O GitHub CLI (`gh`) não está instalado na workstation de dev (verificado em 2026-07-18:
ausente do PATH, de `C:\Program Files\GitHub CLI\` e de `%LOCALAPPDATA%\Programs\GitHub CLI\`).

Reconfirmado em 2026-07-30: ausente do PATH, de `C:\Program Files\GitHub CLI\`,
`%LOCALAPPDATA%\Programs\GitHub CLI\`, chocolatey, scoop e WinGet Links; sem `GH_TOKEN`/
`GITHUB_TOKEN` no ambiente (o remoto é SSH, credential helper `manager`).

**Why:** tentei `gh run list` para checar runs do Actions após push e o binário não existe.

**How to apply — alternativa que funciona para o CI DEV:** o runner `dev-local` roda **nesta
máquina** (`NDD-NOT-10910`, `C:\actions-runner`), então o resultado do job sai do log local, sem
GitHub API:

- `C:\actions-runner\_diag\Worker_*.log` (mais recente) → contém o ref/SHA do run
  (`refs/heads/develop`, sha), o nome do workflow, `Processing step: DisplayName='...'` para cada
  step e `JobRunner] Job result after all job steps finish: Succeeded|Failed`.
- **O `Write-Host` dos steps NÃO fica nesse log** (é streamado pro servidor; não há `_diag\pages`).
  Para o efeito do deploy, olhar o estado real: `Get-Service LayoutParserApi`, timestamp de
  `C:\inetpub\wwwroot\layoutparser\api\LayoutParserApi.dll` e um GET na API.

Para runs de **produção** (runner no `WINSRV2022-LIB`) esse atalho não existe — aí é browser
(github.com/LayoutParser/LayoutParserApi/actions). `git ls-remote origin refs/heads/master` é
read-only e mostra se a master avançou sem precisar de fetch. Ver [[runner-isolation-rollout]].
