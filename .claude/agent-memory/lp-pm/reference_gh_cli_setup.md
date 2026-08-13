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

Related: [[project-execute-candidates-cnhi-gap]]
