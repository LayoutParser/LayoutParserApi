---
name: security-regression-appsettings
description: Senha do SQL segue comitada no appsettings.json apesar de rules/security.md marcar a remoção como concluída — checklist desatualizado
metadata:
  type: project
---

Constatado em 2026-07-18 (branch `feat/lowcode-batch-mode`, arquivo limpo no git status → conteúdo está em HEAD): `appsettings.json` contém `Database:Password` real em texto plano. Últimos commits que tocaram o arquivo têm mensagens antigas ("liberação", "teste", "..").

**Why:** `rules/security.md` marca "Substituir valores por placeholders vazios ✅" — mas isso não é verdade no estado atual do repo (a remediação regrediu ou nunca chegou nesta linha de história). Confiar no checklist sem verificar o arquivo leva a conclusões erradas.

**How to apply:** antes de qualquer decisão que envolva `appsettings.json`/segredos, verificar o arquivo real, não o checklist. Rotação da senha SQL continua pendente (comprometida). No seed de deploy local (2026-07-18) o `appsettings.json` publicado em `C:\inetpub\wwwroot\layoutparser\api` foi **sanitizado** (senha vazia — segredos só via env vars). Acionar @lp-devops para refazer a remediação no repo + atualizar o security.md.
