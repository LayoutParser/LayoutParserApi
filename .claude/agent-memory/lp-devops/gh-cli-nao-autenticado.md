---
name: gh-cli-nao-autenticado
description: gh CLI está instalado e AUTENTICADO desde 2026-08-12 — localização do binário mudou, precisa checar a cada sessão; ajustar PATH no Bash tool
metadata:
  type: project
---

O binário `gh` já apareceu em pelo menos dois caminhos diferentes entre sessões:
`C:\Users\elson.lopes\.local\bin\gh.exe` e `/tmp/gh_extract/bin/gh.exe` (extraído em
diretório temporário — pode não sobreviver a reinícios do sandbox). **Não assuma o caminho
antigo funciona** — rode `where gh 2>/dev/null; find / -maxdepth 4 -iname "gh.exe" 2>/dev/null`
para redescobrir antes de exportar o PATH. Shell state não persiste entre chamadas de Bash,
então o export tem que ser refeito (ou encadeado no mesmo comando) a cada uso.

**Status atual (2026-08-12):** usuário rodou `gh auth login` interativamente e confirmou.
`gh auth status` agora mostra logado como `elson-vinicius-lopes`, escopos `gist, read:org, repo,
workflow`. Primeira PR criada com sucesso após a auth: `gh pr create` para
`feat/identidade-do-bff` → `develop` no repo `LayoutParser/LayoutParserApi`
(https://github.com/LayoutParser/LayoutParserApi/pull/28).

**Why:** tentativa anterior de `gh pr create` (mesma branch, mesmo dia) tinha falhado com exit
code 4 por falta de sessão — usuário resolveu manualmente via `gh auth login`.

**How to apply:** `gh` está pronto para uso normal (`gh pr create`/`gh pr merge`/`gh pr view`
etc.) — só lembrar de exportar o PATH no início de cada sessão de Bash antes de chamar `gh`.
Não é mais necessário checar `gh auth status` como bloqueio a cada vez, mas fazer o check rápido
continua barato caso o token expire. Ver também [[env-gh-cli-ausente]] (nota antiga, já obsoleta,
de que gh não estava nem instalado).
