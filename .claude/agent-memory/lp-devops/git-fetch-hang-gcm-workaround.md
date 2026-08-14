---
name: git-fetch-hang-gcm-workaround
description: git fetch/pull via HTTPS trava (Git Credential Manager tenta prompt interativo/GUI) mesmo com gh autenticado — workaround usando token do gh direto no fetch
metadata:
  type: feedback
---

`git fetch`/`git pull` na origin HTTPS (`https://github.com/LayoutParser/LayoutParserApi.git`)
trava indefinidamente mesmo com `gh` autenticado nesta workstation — o Git Credential Manager
(`credential.helper = manager`) tenta abrir um prompt (provavelmente GUI) que nunca retorna no
ambiente do Bash tool. `GIT_TERMINAL_PROMPT=0` só troca o hang por um erro limpo, não resolve.

**Why:** GCM não reusa a sessão de auth do `gh` CLI automaticamente neste ambiente; o fetch fica
esperando input que nunca chega via Bash tool não-interativo.

**How to apply:** quando `git fetch`/`git pull` travar (timeout > alguns segundos), usar o token
do `gh` diretamente, contornando o credential helper:
```bash
GH_TOKEN=$(gh auth token)
git -c credential.helper= -c http.extraHeader="Authorization: Basic $(printf 'x-access-token:%s' "$GH_TOKEN" | base64 -w0)" fetch origin <branch>
git merge --ff-only FETCH_HEAD
```
Confirmado funcional em 2026-08-13 após merge da PR #72. Ver também [[gh-cli-nao-autenticado]] (gh
já autenticado, PATH precisa export por sessão).
