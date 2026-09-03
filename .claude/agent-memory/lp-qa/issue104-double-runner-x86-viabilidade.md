---
name: issue104-double-runner-x86-viabilidade
description: Avaliação de viabilidade do double x86 do runner LowCode (issue #104) e esqueleto implementado em tools/FakeLowCodeRunner
metadata:
  type: project
---

Viável sem mudança de infraestrutura no runner self-hosted (`ci-dev.yml`/`deploy.yml`,
`[self-hosted, windows, dev-local|production]`, .NET SDK 10.0.x via `actions/setup-dotnet@v6`).

**Por quê funciona:** o runner real (`tools/LowCodeRunner/LayoutParserLowCodeRunner.csproj`) é
`net481`/`PlatformTarget x86` — arquitetura casada com as DLLs Sysmiddle nativas x86, não com
uma exigência do .NET moderno. O double (`tools/FakeLowCodeRunner/`) não carrega nada do
Sysmiddle, então não precisa de net481. A armadilha real não era net481 vs net10, era: um
publish **framework-dependent** x86 em `net10.0` quebraria em runtime no runner de CI porque
`setup-dotnet` só instala o runtime x64 (host), não o runtime x86 correspondente — e não há
esse workload/runtime x86 instalado por padrão no runner self-hosted Windows.

**Mitigação usada (não é infra nova, é config do próprio projeto):** publish **self-contained
`win-x86` + `PublishSingleFile`** — embute o runtime no exe, roda via WOW64 (suporte nativo do
Windows para 32-bit em host 64-bit). Validado nesta sessão: `dotnet publish -c Release` a partir
de `tools/FakeLowCodeRunner/FakeLowCodeRunner.csproj` restaura o runtime pack `win-x86` via NuGet
e gera um `.exe` real (~69MB, PE), mesmo rodando o `dotnet publish` a partir de um host Linux —
ou seja, a única dependência é acesso ao NuGet (já existe, o build normal já depende disso).

**Esqueleto implementado:** `tools/FakeLowCodeRunner/Program.cs` — imita a forma NOMEADA do
contrato real (`tools/LowCodeRunner/RunnerArgs.cs`/`RunnerArgsParser.ParseNomeado`), aceita
`--inputFile`/`--outputFile`/`--package`/`--runnerLogFile`/`--correlationId` e ignora as demais
flags sem falhar o parse. Cenário controlado por env var `FAKE_RUNNER_SCENARIO`
(`success|timeout|malformed_output|nonzero_exit|empty_output|usage_error`), não por argumento —
decisão deliberada porque o chamador real (`LowCodeTransformationService`) não tem como/motivo
passar isso; quem controla o cenário é o ambiente de teste e2e, não a chamada de produção.
Reutiliza os mesmos exit codes do runner real (`RunnerExitCodes`) para exercitar o tratamento de
exit code do lado API sem o binário verdadeiro.

**Pendências fora do escopo desta sessão** (não implementadas, cabem a `@lp-backend-dev`):
- Teste e2e propriamente dito (`TryEnqueueAiCandidateE2ETests.cs`) apontando `LowCode:RunnerPath`
  para o `.exe` publicado do double.
- Step de CI que builda o double antes dos testes e2e (`@lp-devops`, fora do alcance deste agente
  — `.github/workflows/` é exclusivo dele).
- Fidelidade do double é o risco central de longo prazo (já registrado no plano técnico #104): se
  o contrato real do runner mudar (novo argumento, novo formato de saída), o double não acompanha
  sozinho — precisa de revisão manual sempre que `RunnerArgs.cs`/`RunnerArgsParser` mudar.

Commit local: branch `feat/double-runner-x86-viabilidade-104` (a partir de `origin/develop`),
sem push (fora da autoridade de `@lp-qa`).
