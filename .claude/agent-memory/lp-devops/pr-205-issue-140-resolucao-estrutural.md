---
name: pr-205-issue-140-resolucao-estrutural
description: PR #205 (issue #140, motor de resolução estrutural TXT-XML) — checks verdes de primeira, sem falso positivo SCS0018; validação comportamental real contra LowCodeRunner ficou pendente (ambiente Windows-only)
metadata:
  type: project
---

PR #205 (`feat/resolucao-estrutural-txt-xml-140` → `develop`) criado em 2026-08-27: motor de
resolução estrutural TXT↔XML via catálogo XSD SEFAZ NF-e para a issue #140. Branch já estava em
cima de `origin/develop` (incorporava os merges de #200/#201), sem necessidade de rebase.

Build 0 erros, `dotnet test` 461 passando / 4 falhas pré-existentes de path Windows×Linux
(mesmo padrão de sempre, não relacionadas). Todos os 4 checks (`build`, `build-and-test`,
`dependency-review`, `gitleaks-scan`) passaram de primeira — diferente dos PRs #198/#200/#203
([[pr-203-issue-138-sectionmappings-fase0]]), aqui NÃO houve falso positivo SCS0018.

**Limitação explícita registrada no PR:** a validação comportamental contra o `LowCodeRunner`
real (critério de aceite original da #140) não foi possível em WSL/Linux (executável
Windows-only net481). Foi substituída por 20+ testes determinísticos contra fixtures, mas a
validação comportamental real fica pendente — ação do dono ou de agente com acesso a Windows.

**Não mergeado** (fora do escopo de devops). Repassado ao dono se/quando a issue #141 (que
depende de #139 E #140 "concluídas e validadas") pode iniciar sem a validação comportamental
completa — decisão de escopo/risco que não é do devops tomar sozinho.
