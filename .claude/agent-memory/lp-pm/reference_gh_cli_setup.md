---
name: reference-gh-cli-setup
description: Onde está o gh CLI, autenticação e convenções de criação de issue já validadas neste repo
metadata:
  type: reference
---

- `gh` CLI está em `C:\Users\elson.lopes\.local\bin\gh.exe` (caminho completo — não está no PATH). Usar sempre o caminho absoluto nas chamadas Bash.
- Repo alvo: `LayoutParser/LayoutParserApi`. Autenticado, com escopo `read:project` já concedido.
- Checar duplicata antes de criar: `gh issue list --repo LayoutParser/LayoutParserApi --search "<termos>" --state all`.
- Dono já autorizou criação direta de issues (sem rascunho prévio) quando a fonte é um diagnóstico técnico bem documentado (ex.: memória de outro agente `@lp-*`). Formato de corpo: `## Contexto` (com link pro arquivo de origem em `.claude/agent-memory/<agente>/`), `## O que falta`, `## Critério de aceite` (checklist), `## Dono natural`, `## Severidade` (ou `## Por que agora` para stories). Ver issues #30-#40 como padrão de capricho.
- GitHub Project confirmado em uso: **Project #2** (`--owner LayoutParser`), project-id `PVT_kwDODnBfYs4BgMpG`. Field `Status` id `PVTSSF_lADODnBfYs4BgMpGzhaaEnU`, opções: Todo=`f75ad846`, In Progress=`47fc9ee4`, Done=`98236657`. Para achar o item-id de uma issue: `gh project item-list 2 --owner LayoutParser --format json` e filtrar por `content.number`. Editar com `gh project item-edit --id <item-id> --field-id <field-id> --project-id <project-id> --single-select-option-id <option-id>`.
- Project #2 também já tem os campos `Tipo` (field-id `PVTSSF_lADODnBfYs4BgMpGzhaaEyM`; opções bug=`fb117f1c`, tech-debt=`832e19ca`, story=`b1173f83`, gate=`7a4cbe35`, investigação=`f5bff30b`) e `Dono` (field-id `PVTSSF_lADODnBfYs4BgMpGzhaaEzE`; opções lp-backend-dev=`c290c76b`, lp-parser-llm=`2cab763a`, lp-devops=`9a9f036a`, lp-architect=`2e02a2d2`, lp-doc=`cd43be44`, lp-qa=`bc99a395`) — usar `gh project field-list 2 --owner LayoutParser --format json` se algum id mudar/expirar.
- Adicionar item novo ao board: `gh project item-add 2 --owner LayoutParser --url <issue-url> --format json` retorna o `id` do item (`PVTI_...`) direto, sem precisar de `item-list` depois.

Related: [[project-execute-candidates-cnhi-gap]]
