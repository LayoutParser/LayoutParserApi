# Triagem CodeQL — 166 alertas abertos (2026-08-16)

Contexto: repos voltaram a público em 2026-08-15, CodeQL passou a rodar e reportar sem baseline
próprio (diferente do SecurityCodeScan, que já tem `security-code-scan-baseline.json`).

## 1. Os 2 critical — `cs/command-line-injection` (CWE-78/88) — REAIS, corrigir

| # | Arquivo:linha | Risco |
|---|---|---|
| 7 | `Services/Transformation/LowCode/LowCodeTransformationService.cs:110` | `Arguments = string.Join(" ", args)` monta a linha de comando do runner LowCode com `mapperId`/`mapperName`/`package`/`fileName` vindos da requisição de parse, só protegidos por `Quote()` (aspas simples, sem escapar aspas internas nem `%`/`^`). Um nome de mapper/arquivo malicioso pode quebrar o parsing de argumentos do runner (`CreateProcess` do Windows faz parsing próprio de `Arguments` como string única). |
| 6 | `Services/Database/DecryptionService.cs:149` | `BuildArgs(inputFile, outputFile, corr)` para o `.exe` de descriptografia legado. `inputFile`/`outputFile` são paths temporários gerados internamente (`Guid`), risco menor que o #7, mas mesma classe de vulnerabilidade — merece o mesmo tratamento por consistência. |

**Recomendação:** trocar `Arguments` (string única) por `ArgumentList` (`IList<string>`) do
`ProcessStartInfo` nos dois pontos — elimina a necessidade de `Quote()`/parsing manual, o
.NET/Windows cuida do escaping por argumento. É uma correção pequena e mecânica, não redesenho.
Dono: `@lp-backend-dev`.

## 2. Os 17 high — `cs/path-injection` — MESMA CLASSE do SCS0018 já no baseline

Todos os 17 alertas high são "Uncontrolled data used in path expression" em
`XsdValidationService.cs`, `TransformationPipelineService.cs`, `TransformationLearningService.cs`,
`TransformationValidatorService.cs`, `AutomatedTransformationTestService.cs` — mesmos arquivos e
mesmo padrão (nome de layout/mapper vindo do banco/request usado em `Path.Combine`) que os 26×
SCS0018 já represados no `security-code-scan-baseline.json`. Não é achado novo — é a mesma dívida
técnica vista por um engine diferente.

**Recomendação:** não corrigir 17 pontos agora como se fossem urgentes. Criar baseline equivalente
para CodeQL (dismissal em massa com `used in tests`/`won't fix` referenciando o baseline existente,
ou script análogo ao SecurityCodeScan) e tratar como a mesma dívida já rastreada. Se algum dia se
justificar corrigir path traversal de verdade, fazer nos dois engines ao mesmo tempo (sanitização
centralizada de nome de layout/mapper, não patch por arquivo).

## 3. Os ~147 medium — majoritariamente `cs/log-forging` (139/147) — FALSO POSITIVO EM MASSA

Confirmado: 139 de 147 são `cs/log-forging` (CWE-117). O projeto usa logging estruturado do
Serilog (`_logger.LogInformation("Parse {Layout}", name)`), que é exatamente a mitigação padrão
para log injection — o valor vira um campo estruturado, não é concatenado na string de mensagem
que pode conter `\r\n`/ANSI para forjar linhas de log. O CodeQL `cs/log-forging` não modela
`Message Templates` do Serilog/`ILogger` e trata qualquer parâmetro rastreável a input externo
como injeção, mesmo passado via placeholder `{Param}`. **Isso é ruído, não 139 bugs reais.**
Os 8 medium restantes (`cs/exposure-of-sensitive-information` ×3, `cs/xml/missing-validation` ×2,
`actions/*` ×3) são achados distintos, pequenos — revisar individualmente, não fazem parte do
padrão de ruído do log-forging.

**Recomendação:** dismissal em massa dos 139 `cs/log-forging` como "false positive" (motivo:
logging estruturado via `ILogger`/Serilog, não concatenação), com uma nota linkando este
documento. Revisar os 8 restantes um a um.

## Ação imediata proposta

1. `@lp-backend-dev`: corrigir os 2 critical (`ArgumentList` em vez de `Arguments`) — pequeno, alto valor.
2. `@lp-devops`: dismissal em massa dos 139 `cs/log-forging` (falso positivo documentado) + criar
   baseline/dismissal para os 17 `cs/path-injection` (dívida já conhecida, mesmo padrão do SCS0018).
3. Revisar isoladamente os 8 medium restantes (`exposure-of-sensitive-information`,
   `xml/missing-validation`, `actions/*`) — não cobertos por este documento em detalhe.
