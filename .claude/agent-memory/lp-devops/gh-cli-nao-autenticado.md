---
name: gh-cli-nao-autenticado
description: gh CLI autenticado mas fora do PATH padrão — localização do binário e como invocar por sessão Bash
metadata:
  type: reference
---

`gh` (v2.97.0) está autenticado como `elson-vinicius-lopes` (keyring, scopes gist/project/read:org/repo/workflow),
mas **não está no PATH padrão** da sessão Bash. Binário real:
`C:\Users\elson.lopes\AppData\Local\Temp\gh_extract\bin\gh.exe`.

**Como usar em cada sessão Bash nova:**
```bash
export PATH="$PATH:/c/Users/elson.lopes/AppData/Local/Temp/gh_extract/bin"
gh pr create / gh pr merge / gh pr view ...
```

Confirmado funcionando em 2026-08-16: `gh pr create` (PR #143, feat/dsl-mapper-contexto-ia → develop),
`gh pr edit`, `gh pr merge --merge`, `gh pr view --json` todos operacionais com esse PATH.

Se `gh pr create` disser "PR já existe", é sinal de que outro agente/sessão já criou — confira com
`gh pr view <num>` antes de tentar criar de novo.
