---
name: runner-isolation-rollout
description: Isolamento runners prod (label 'production') x dev ('dev-local') — ordem de rollout crítica e pendências (commits locais 78e950f/7d24128, sem push)
metadata:
  type: project
---

Isolamento de runners decidido pela arquiteta (2026-07-18): deploys de produção exigem label `production` (runner WINSRV2022-LIB, org LayoutParser, 172.25.32.42); CI de dev exige `dev-local` (runner na máquina local NDD-NOT-10910, ainda não registrado).

**Estado:** commits LOCAIS sem push — Api `78e950f` (branch `feat/lowcode-batch-mode`), React `7d24128` (branch `main`). Lib NÃO alterada (trigger-deploy.yml roda em `ubuntu-latest`, sem risco de cair no dev-local).

**Why:** com `[self-hosted, windows]` sem label extra, um deploy de produção poderia executar na máquina local do usuário assim que o runner dev fosse registrado.

**How to apply — ordem de rollout obrigatória antes de qualquer push/merge:**
1. Usuário adiciona a label `production` ao runner WINSRV2022-LIB pela UI do GitHub **antes** de o novo deploy.yml chegar a master/main — senão deploys ficam presos em "waiting for a runner".
2. Registrar o runner dev-local com label `dev-local` antes de pushar branches `develop`/`feat/**` (o ci-dev.yml dispara nelas e ficaria em fila sem runner).
3. No React o commit foi direto na `main` — o push dela dispara o deploy de produção; empacotar com cuidado.

Detalhe técnico validado no dev-local: a Lib (csproj legado net4.8.1, sem PackageReference) compila com `dotnet build` puro — não usar `microsoft/setup-msbuild` no ci-dev (a máquina não tem VS Build Tools). O ci-dev builda a Lib em **Debug** para satisfazer o HintPath `..\LayoutParserLib\bin\Debug` do csproj da API.
