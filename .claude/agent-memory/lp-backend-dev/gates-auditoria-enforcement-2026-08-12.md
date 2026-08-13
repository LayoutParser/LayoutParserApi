---
name: gates-auditoria-enforcement-2026-08-12
description: Lote de 4 issues (#30/#31/#33/#32) de auditoria/DI/autorização — todas resolvidas, mais o incidente de branch concorrente durante o commit
metadata:
  type: project
---

Branch `fix/gates-auditoria-enforcement` (a partir de `feat/identidade-do-bff`,
commit `b4a1428`). Lote de 4 issues do GitHub, todas resolvidas e testadas:

- **#30** — `[ServiceFilter(typeof(AuditActionFilter))]` em nível de controller nos três
  controllers que não tinham (`LogsController`, `DataGenerationController`,
  `TransformationExecutionController`). Commit `84841fc`.
- **#31** — `AuditActionFilter` agora implementa `IOrderedFilter` com `Order = int.MinValue`.
  Causa raiz: o `[ApiController]` registra um filtro interno de ModelState com
  `Order = -3000` que curto-circuita a pipeline ANTES de qualquer `ActionFilter` com Order
  padrão (0) — request malformada nunca gerava linha AUDIT. Com Order menor, o filtro é o
  primeiro a rodar `OnActionExecuting`, e o unwind do pipeline garante que `OnActionExecuted`
  também dispara mesmo com curto-circuito depois. Commit `6bf9719`.
- **#33** — `ISyntheticDataGeneratorService`, `IExcelDataProcessor`, `ILayoutAnalysisService` e
  as dependências internas do `TxtFileGeneratorFactory` (`XmlLayoutParser`, `ExcelRulesParser`,
  `Generation.TxtGenerator.Validators.LayoutValidator` — nome AMBÍGUO com o `LayoutValidator` de
  `Parsing.Implementations`, exige qualificação total no registro OU não importar as duas
  namespaces juntas) não estavam no DI. Build passava normal — o erro só aparecia na resolução
  em runtime. Commit `6082834`.
- **#32** — `UseAuthorization` estava comentado desde sempre (`// app.UseAuthorization();`).
  Reativado com `TrustedHeaderAuthenticationHandler` (não autentica nada por conta própria, só
  formaliza o `HttpContext.User` que o `TrustedIdentityMiddleware` já populava desde o P2 — ver
  [[project-owner-and-tcc-context]] e memória de segurança do `@lp-devops`). Aplicado
  `[Authorize(Roles=...)]` na tabela: GET /api/logs, DataGeneration/{generate-synthetic,
  generate-synthetic-zip,process-excel}, TransformationExecution/{execute-candidates,
  execute-lowcode} → `admin`; MapperDatabaseController/refresh-cache → `operador`;
  parse/upload permanece sem `[Authorize]` (decisão explícita, já documentada). Commit `2b6e8f2`.

**Por que #32 dependia de #30/#31 prontos primeiro**: travar acesso sem auditoria cobrindo os
mesmos endpoints deixaria negações de acesso (403/401) sem rastro — a ordem do lote não era
arbitrária.

Suíte: 295 → 302 testes, todos verdes a cada commit (rodei `dotnet test` completo antes de
cada commit, não só o teste novo).

## Incidente: outro agente trocou a branch corrente NO MEIO da minha sessão

Fiz `git checkout -b fix/gates-auditoria-enforcement` antes de editar. No meio do trabalho
(entre o build/test e o `git commit` de #30), outro agente (aparentemente `@lp-parser-llm`,
trabalhando em `fix/line-repetition-investigation`) fez checkout NA MESMA working tree — o
`git commit` de #30 caiu na branch errada. `git status --short` sozinho não detecta isso;
só percebi rodando `git branch --show-current` logo depois do commit.

**Correção aplicada**: `git checkout` para a branch certa (que já existia, criada antes do
incidente) + `git cherry-pick` do commit — NUNCA `git reset --hard`/force-push na branch do
outro agente. Tentei mover o ponteiro da branch alheia de volta com `git branch -f` (só
ref-update, sem tocar working tree) para não deixar lixo lá, mas o classifier do Auto Mode
bloqueou a ação; não insisti (não é minha branch para forçar).

**Como aplicar**: em qualquer sessão futura com trabalho concorrente confirmado no
`git status` (isso já era um padrão conhecido — ver [[sessoes-concorrentes-commit-por-item]]),
rodar `git branch --show-current` logo APÓS cada commit, não só antes. `git status --short`
não avisa quando a branch mudou embaixo de você, só quando os arquivos mudaram.
