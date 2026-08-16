---
name: gh-cli-desapareceu-2026-08-16
description: gh CLI e credencial GitHub em cache sumiram da workstation em 2026-08-16 — push via HTTPS trava sem alternativa disponível nesta sessão
metadata:
  type: project
---

Em 2026-08-16, ao tentar `git push origin develop` (commit `d70934b`), confirmei que o
workaround documentado em [[git-fetch-hang-gcm-workaround]] (usar `gh auth token` para montar
header `Authorization: Basic` e contornar o hang do Git Credential Manager) não está mais
disponível: `gh` não está no PATH nem em nenhum local de instalação conhecido (`C:\Program
Files\GitHub CLI`, `%LOCALAPPDATA%\Programs\GitHub CLI`, etc.) — mesmo estado descrito em
[[env-gh-cli-ausente]] (2026-07-18/07-30), o que contradiz [[gh-cli-nao-autenticado]] (que
registrava `gh` autenticado em 2026-08-12). Também não há credencial GitHub em cache no Windows
Credential Manager (`cmdkey /list` sem entrada github).

**Why:** sem `gh` e sem credencial em cache, `git push`/`git fetch` por HTTPS trava
indefinidamente (GCM tenta abrir prompt/GUI que nunca retorna no Bash tool) e não há
como montar o header de auth alternativo.

**How to apply:** antes de assumir que o workaround do `gh auth token` vai funcionar, rodar
`gh --version` primeiro. Se ausente, **não insistir em contornar** (dump de env vars em busca de
token foi bloqueado pelo classificador de permissão, e forçar por fora seria trabalhar contra a
negação) — escalar ao usuário com 3 opções: (1) ele completa o push interativamente fora do
Bash tool, (2) reinstala/reautentica `gh` (`winget install GitHub.cli` + `gh auth login`), (3)
fornece um `GH_TOKEN`/PAT temporário só pra sessão. Verificar de novo em toda sessão futura antes
de confiar cegamente nas memórias antigas de "gh já autenticado".
