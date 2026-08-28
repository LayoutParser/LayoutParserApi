---
name: pr-198-ci-scs0018-bloqueado
description: PR #198 — CI bloqueado por SCS0018 (falso positivo por deslocamento de linha), RESOLVIDO em 2026-08-27 com autorização explícita do usuário
metadata:
  type: project
---

PR #198 (`feat/contrato-linha-vazia-e-progresso` → `develop`) recebeu 4 commits novos
(doc, exposição de `lineInfos`, testes QA, fix de `IsDeclaredEmpty`) pushados em 2026-08-27
(fast-forward `0d2a05a..07ce492`). `dotnet build`/`dotnet test` locais passaram limpos.

O CI remoto falhou em `build`/`build-and-test` no gate SecurityCodeScan: 2 achados SCS0018
fora do baseline em `Controllers/ParseController.cs:550` e
`Services/Transformation/LowCode/LowCodeTransformationStore.cs:286`.

**RESOLVIDO em 2026-08-27:** o usuário trouxe a análise já feita (confirmada contra o log real
do CI) de que eram falsos positivos por **deslocamento de linha**, não código novo — o diff do
PR contra `origin/develop` só adiciona strings de status/`lineInfos` *antes* dos dois trechos
flagados (mesmo `FileStream`/`File.ReadAllTextAsync` de sempre, já guardado por
`SafePathResolver.IsInsideBase`), empurrando os números de linha no baseline (526→550,
275→286). Com autorização explícita do usuário e o diff do baseline conferido (só os 2 números
de linha mudaram, mesmo `code`/`file`), `@lp-devops` editou `security-code-scan-baseline.json`,
commitou (`56f3742`) e fez push. Todos os checks do PR #198 ficaram verdes
(`build`, `build-and-test`, `dependency-review`, `gitleaks-scan`).

**Why:** a regra geral continua sendo não editar o baseline sem triagem de quem tocou o código
— mas aqui a triagem já tinha sido feita e confirmada pelo usuário, então a autorização supre a
delegação a `@lp-backend-dev`/`@lp-parser-llm`.

**How to apply:** se aparecer um novo achado SCS0018 "por deslocamento" no futuro, o padrão de
diagnóstico é o mesmo: comparar `git diff` do PR contra o base branch nos arquivos flagados —
se a mudança real não toca construção de path/arquivo, é forte candidato a falso positivo por
linha deslocada. Ainda assim, só editar o baseline sozinho com autorização explícita do usuário
(como aqui) ou após confirmação de `@lp-backend-dev`/`@lp-parser-llm`.
