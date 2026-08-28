---
name: pr-200-ci-scs0018-bloqueado
description: PR #200 (issue #86 pathwayDiagnostics) — CI bloqueado por SCS0018, mesmo padrão de falso positivo por deslocamento de linha do PR #198, NÃO resolvido (sem autorização explícita nesta sessão)
metadata:
  type: project
---

PR #200 (`feat/execute-candidates-diagnostico-estruturado-86` → `develop`, issue
`LayoutParserReact#86`) — `dotnet build`/`dotnet test` locais passaram limpos (399 passando,
4 falhas pré-existentes de path Windows×Linux, iguais às de sempre).

CI remoto falhou em `build` e `build-and-test` no gate SecurityCodeScan: 1 achado SCS0018 novo
fora do baseline em `Services/XmlAnalysis/TransformationPipelineService.cs:393`.

**Diagnóstico feito (mesmo padrão do PR #198, ver [[pr-198-ci-scs0018-bloqueado]]):** é falso
positivo por deslocamento de linha, não código novo. `git diff origin/develop...HEAD` no arquivo
mostra que o PR só insere `result.ErrorCode = "xsl_not_found"`/`"map_not_found"` **antes** do
trecho flagado — o `File.ReadAllTextAsync(mapPath, Encoding.UTF8)` em si não mudou. Confirmado:
`git show origin/develop:...` tem esse mesmo `ReadAllTextAsync` na linha 390, que já está no
baseline (`security-code-scan-baseline.json` linha 61, `"line": 390`). O PR empurrou para 393.

**NÃO editado nesta sessão** — a tarefa que originou este PR instruiu explicitamente "reporte o
que falhar, sem tentar resolver sozinho antes de reportar", então o baseline não foi tocado.
Diferente do PR #198, aqui não havia autorização explícita do usuário pra editar o baseline.

**How to apply:** se o usuário confirmar que é falso positivo (mesmo padrão já visto), a correção
é trivial: mudar `"line": 390` para `"line": 393` em `security-code-scan-baseline.json` para
`Services/XmlAnalysis/TransformationPipelineService.cs`, commitar e push. Sempre confirmar
`git diff` do arquivo primeiro — só editar baseline sozinho com autorização explícita do usuário
ou confirmação de `@lp-backend-dev`/`@lp-parser-llm`.
