---
name: env-gh-cli-ausente
description: gh CLI NÃO está instalado na workstation de dev — status de runs/PRs só via browser ou API com token
metadata:
  type: project
---

O GitHub CLI (`gh`) não está instalado na workstation de dev (verificado em 2026-07-18:
ausente do PATH, de `C:\Program Files\GitHub CLI\` e de `%LOCALAPPDATA%\Programs\GitHub CLI\`).

**Why:** tentei `gh run list` para checar runs do Actions após push e o binário não existe.
**How to apply:** em missões que precisem de status de runs, PRs ou API do GitHub, não planejar
passos com `gh` — pedir ao usuário para checar no browser (github.com/LayoutParser/LayoutParserApi/actions)
ou instalar o gh antes. Push/pull via git funcionam normalmente (SSH). Ver [[runner-isolation-rollout]].
