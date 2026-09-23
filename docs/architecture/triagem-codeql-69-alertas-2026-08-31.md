# Triagem CodeQL — 69 alertas (2026-08-31)

Repositório ficou público de novo e o CodeQL nativo (default setup) rodou pela primeira vez,
abrindo 69 alertas `medium`: 66 `cs/log-forging` (CWE-117) + 3 `cs/exposure-of-sensitive-information`
(CWE-359).

## Causa raiz — `cs/log-forging` (66 alertas)

O projeto já segue o padrão de logging estruturado (`_logger.LogX("... {Param}", valor)`, nunca
interpolação/concatenação) — **não é** o defeito que o CodeQL está sinalizando. O CodeQL flagra
mesmo parâmetros estruturados quando o **valor em si** pode conter `\r`/`\n` e nunca é
sanitizado antes de virar argumento de log: um valor vindo do request com uma quebra de linha
crua forja uma linha de log falsa (2 origens confirmadas de risco real neste projeto, não
teórico):

1. **`X-Correlation-ID`** (header aceito do cliente, `Program.cs`) — ecoado em praticamente todo
   log da API via `Services.Logging.CorrelationContext.CurrentId`/Serilog `LogContext`. Era o
   maior raio de exposição: um único ponto vazando pra dezenas de call sites.
2. **`LayoutName`/`LayoutGuid`/`mapperGuid`/`ticket`/mensagens de erro que ecoam nomes** — vêm do
   corpo do request em `TransformationExecutionController`, `RepairOrchestratorXslSynthesizerService`,
   `AiTransformationCandidateService`, etc.

Já existia o helper certo para isso — `Services/Logging/LogMessageSanitizer.cs` (criado em
2026-07-30 pela QA/Quinn depois de um incidente real: um stack trace colado num campo de
observação injetou uma "geração fantasma" no painel de métricas de IA). `Sanitize(string?)`
achata CRLF/LF/CR e trunca em 4000 chars por padrão. **REUSADO** em vez de recriar — decisão IDS.

## Correção aplicada

### 1) Fix de raiz único — `Program.cs` (middleware de CorrelationId)

```csharp
correlationId = LayoutParserApi.Services.Logging.LogMessageSanitizer.Sanitize(correlationId, maxLength: 200);
```

Saneia o `X-Correlation-ID` vindo do cliente **uma única vez**, na origem — cobre todo log que
usa `CorrelationContext.CurrentId`/`LogContext` no resto da API, sem precisar tocar em cada call
site individual que loga `{CorrelationId}`.

### 2) Sanitização local nos demais pontos (47 linhas únicas flagadas em 10 arquivos)

Padrão aplicado: declarar uma variável `safe*` saneada **uma vez por método** (não por log call)
logo onde o valor tainted entra no escopo, e trocar só as referências usadas em `_logger.Log*`
— o valor cru continua intacto em lógica de negócio (busca no catálogo, comparação, montagem de
ticket, `EnqueueAsync`), onde forjar log não se aplica.

| Arquivo | Alertas | O que foi saneado |
|---|---|---|
| `Controllers/TransformationExecutionController.cs` | 23 linhas únicas (41 alertas, dobrados por LayoutName+LayoutGuid no mesmo call) | `request.LayoutName`, `resolvedLayoutGuid`, `sharedParsingResult.ErrorMessage`, `layoutName` local, `ticket` |
| `Services/Transformation/Ai/RepairOrchestratorXslSynthesizerService.cs` | 9 | `mapperGuid`, `layoutName` |
| `Controllers/ParseController.cs` | 4 (3 são `correlationId`, cobertos pelo fix #1; 1 é `layoutDirectory`) | `layoutDirectory` (derivado de `layoutName` do form) |
| `Services/Implementations/AuditLogger .cs` | 2 (mensagem cobre 2 métodos, 10 campos no total) | `AuditLogEntry`/`LogEntry` inteiros — é o próprio sink de auditoria, todo campo é do request |
| `Services/Identity/IdentityWorkspaceService.cs` | 1 (2 params no mesmo call) | `provider`, `tenant` (claims OIDC) |
| `Services/Database/SqlIdentityWorkspaceStore.cs` | 1 | `provider` |
| `Services/Transformation/Ai/AiTransformationCandidateService.cs` | 2 | `mapperGuid`, `ticket` |
| `Services/Transformation/TransformationValidatorService.cs` | 1 | `layoutName` |
| `Services/Testing/AutomatedTransformationTestService.cs` | 2 | `examplesDirectory`, `layoutName` |
| `Services/Database/DecryptionService.cs` | 2 | `correlationId` (defesa em profundidade — método também chamável fora do pipeline HTTP) |

Nenhum contrato de endpoint mudou — a saneamento é só no que vai pro log, nunca no que volta na
resposta HTTP.

## Veredito — `cs/exposure-of-sensitive-information` (3 alertas)

Todos os 3 apontam para o **mesmo método gerador**: `RandomGenerator.GenerateRandomEmail`
(mensagem do CodeQL confirma: *"Private data returned by call to method GenerateRandomEmail is
written to an external location"*), consumido por `TxtFileGeneratorService`
(`Services/Generation/TxtGenerator/`) — o gerador de **dado sintético de teste** (issue de
geração de TXT/NFe fabricado para o TCC), não um fluxo de dado real de cliente.

| # | Local | Nível | Veredito |
|---|---|---|---|
| 8 | `Generators/RandomGenerator.cs:44` | Debug | **Falso positivo** — valor fabricado pelo próprio gerador aleatório |
| 9 | `TxtFileGeneratorService.cs:93` | Warning | **Falso positivo** — mensagem de erro de validação de um TXT sintético já gerado |
| 10 | `TxtFileGeneratorService.cs:187` | Debug | **Falso positivo** — valor de campo gerado sinteticamente na composição da linha |

Critério de "sensível de verdade" usado (`.claude/rules/security.md`): dado real de cliente,
segredo ou credencial. Aqui não há nenhum — é o próprio motor de geração de dados de teste
(`RandomGenerator`) fabricando um e-mail para preencher um campo de layout, propositalmente
falso. **Descartados** via API do GitHub (`dismissed_reason=false positive`), alertas #8, #9, #10.

## Build/test

```
dotnet build   → 0 Errors (663 warnings pré-existentes, nenhum novo)
dotnet test    → 482 passed, 0 failed
```

## Arquivos alterados

- `Program.cs`
- `Controllers/TransformationExecutionController.cs`
- `Controllers/ParseController.cs`
- `Services/Transformation/Ai/RepairOrchestratorXslSynthesizerService.cs`
- `Services/Transformation/Ai/AiTransformationCandidateService.cs`
- `Services/Implementations/AuditLogger .cs`
- `Services/Identity/IdentityWorkspaceService.cs`
- `Services/Database/SqlIdentityWorkspaceStore.cs`
- `Services/Transformation/TransformationValidatorService.cs`
- `Services/Testing/AutomatedTransformationTestService.cs`
- `Services/Database/DecryptionService.cs`

Nenhum arquivo novo criado — `LogMessageSanitizer` já existia e foi reusado (decisão IDS:
REUSAR, não CRIAR).
