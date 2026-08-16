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

**Duas armadilhas medidas em 2026-08-14** (tentativa de fetch a partir de um worktree isolado):

1. `Authorization: Bearer <token>` **não funciona** para o transporte git-http — o servidor devolve
   401 e o git cai em "could not read Username". Tem que ser `Basic` com base64 de
   `x-access-token:<token>`, exatamente como está no bloco acima. Não improvise o esquema.
2. Em sessão de agente **isolada em worktree**, o comando composto (`$(...)`/pipe com `base64`)
   pode ser barrado pelo classificador de permissão do Bash tool. Nesse caso não há fetch: siga
   com o ref `origin/<branch>` que já existe localmente e **registre no relatório** que a base
   pode estar desatualizada, em vez de ramificar do HEAD errado.

**Confirmado 2026-08-15: o mesmo hang/workaround vale para `git push`, não só fetch/pull.**
`git push origin develop` sem o header travou 2min e foi morto pelo timeout do Bash tool; o
mesmo comando com `git -c http.extraHeader="Authorization: Basic ..."` funcionou de primeira.
Isso também explica handoffs onde outro agente reporta "já fiz `git push -u origin <branch>`"
mas a branch **não aparece no remoto** (`git branch -a`/`gh api .../branches` não lista) — o
push dele provavelmente travou do mesmo jeito e nunca completou. Sempre confirmar
`git ls-remote origin <branch>` ou `gh api repos/.../branches` antes de assumir que um push de
handoff foi bem-sucedido; se não estiver lá, refazer com o header.
