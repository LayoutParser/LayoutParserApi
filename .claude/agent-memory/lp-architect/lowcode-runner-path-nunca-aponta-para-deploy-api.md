---
name: lowcode-runner-path-nunca-aponta-para-deploy-api
description: LowCode__RunnerPath setado manualmente para <deploy>\api\LayoutParserLowCodeRunner.exe está estruturalmente errado — deploy.yml nunca publica ali, publica na Bin do Sysmiddle via LOWCODE_RUNNER_PATH_RESOLVED
metadata:
  type: project
---

Confirmação em produção (2026-08-15, log real): `Win32Exception (2)` ao tentar iniciar
`C:\inetpub\wwwroot\layoutparser\api\LayoutParserLowCodeRunner.exe` — arquivo não existe nesse
caminho. Isso fecha a suspeita original de 2026-08-09 (memória equivalente pode não estar presente
neste worktree/branch — ver `docs/architecture/diagnostico-mapper-nao-encontrado-producao-2026-08-15.md`,
seção "Achado adicional").

**Causa raiz:** `<deploy>\api\` é onde a API é publicada, não onde o runner pode viver. O runner
(`LayoutParserLowCodeRunner.exe`, net481/x86) só executa de dentro de uma Bin **completa** do
produto de terceiros Sysmiddle/AppConnector (resolve dependências ao lado do próprio `.exe`).
`.github/workflows/deploy.yml:364-441` já tem um step ("Publicar runner low-code na Bin do
Sysmiddle") que varre o host, acha a Bin apta (`SysMiddle.Base.dll` + `log4net` 2.x), copia o `.exe`
pra lá e expõe `$env:LOWCODE_RUNNER_PATH_RESOLVED` — consumido automaticamente pelo step seguinte
para popular `LowCode__RunnerPath` no `Environment` do serviço Windows. O dono setou o valor
manualmente com o caminho errado, por cima (ou na ausência) desse mecanismo automático.

**Why:** o comentário do próprio deploy.yml (linha 368) já avisa "NAO copiando para `<deploy>\api`:
nao adiantaria" — a config `RunnerPath` no `appsettings.json` do repo (que aponta pra `<deploy>\api\`)
é só o *default* de referência, nunca o valor real esperado em produção.

**How to apply:** não reintroduzir/reaplicar `LowCode__RunnerPath` manual apontando pra
`<deploy>\api\`. Se o pathway low-code continuar indisponível após reexecutar o deploy.yml, verificar
primeiro se alguma Bin apta foi encontrada no host (log do step) — se não, é ausência de instância
AppConnector completa no host de produção, infraestrutura de terceiro, não bug de código/workflow.
