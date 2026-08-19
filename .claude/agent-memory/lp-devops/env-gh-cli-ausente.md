---
name: env-gh-cli-ausente
description: gh CLI ATUALMENTE instalado em C:\Users\elson.lopes\.local\bin\gh.exe (não no PATH por padrão) — histórico de "ausência" abaixo é anterior a essa instalação
metadata:
  type: project
---

**Atualização 2026-08-17:** `gh.exe` está presente em `C:\Users\elson.lopes\.local\bin\gh.exe`
(v2.97.0, autenticado como `elson-vinicius-lopes`, token com escopos `gist, project, read:org,
repo, workflow`) — não aparece com `which gh` porque essa pasta não está no `$PATH` padrão da
sessão Bash. Para usar: `export PATH="$PATH:/c/Users/elson.lopes/.local/bin"` no início da
sessão. Usado com sucesso para `gh api .../code-scanning/alerts` (dismissal em massa de alertas
CodeQL). O relato de "ausência" abaixo é histórico (2026-07-18/30) e pode estar desatualizado —
sempre checar esse caminho primeiro antes de assumir que `gh` não existe.

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

**Como datar um deploy de produção sem `gh` e sem acesso ao filesystem do servidor**
(descoberto em 2026-07-31; o `.42` não é administrável daqui — ver [[prod-42-acesso-bloqueado]]):
a própria API entrega a evidência pelo endpoint de logs unificados.

1. Escolha uma string que **só existe no código novo** (ex.: uma mensagem de log introduzida
   pelo commit em questão).
2. `GET http://172.25.32.42:5000/api/logs?search=<string>&pageSize=1` → `totalCount` = nº de
   ocorrências; `items[0].timestamp` = a mais recente.
3. `GET ...&page=<totalCount>` → a ocorrência **mais antiga** = limite superior para o instante
   em que o binário novo entrou no ar.

Casa `git log -S'<string>'` (qual commit introduziu) com `git ls-remote origin refs/heads/master`
e você data o deploy sem tocar no servidor. Serve também para provar o contrário: ausência total
da string = binário velho ainda rodando.
