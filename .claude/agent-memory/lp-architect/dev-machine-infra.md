---
name: dev-machine-infra
description: Infra da máquina dev NDD-NOT-10910 — runner como serviço LocalSystem, deploy em C:\inetpub\wwwroot\layoutparser, elevação só via UAC com conta nddraiz
metadata:
  type: project
---

Estado montado em 2026-07-18 na máquina dev `NDD-NOT-10910` (usuário `NDDIGITAL\elson.lopes`, SID `S-1-5-21-606747145-1844237615-1801674531-53219`):

- **Runner GitHub Actions é serviço Windows**: `actions.runner.LayoutParser.NDD-NOT-10910`, binPath `C:\actions-runner\bin\RunnerService.exe`, **LocalSystem**, Automatic, failure actions restart 60s x3. `C:\actions-runner\.service` contém o nome. Não abrir mais `run.cmd` manual — conflita com a sessão do serviço.
- **Elevação**: `elson.lopes` NÃO é admin; sudo do Windows desabilitado; gsudo ausente. Caminho que funciona: script `.ps1` em caminho Windows + `Start-Process powershell -Verb RunAs` (UAC aprovado com a conta `NDD-NOT-10910\nddraiz`). Atenção: `$env:USERNAME` no contexto elevado = `nddraiz`, não `elson.lopes` — usar SID em icacls.
- **Deploy prod-like**: `C:\inetpub\wwwroot\layoutparser\{api, backups, Exemplo}` — anatomia inferida dos paths hardcoded no `appsettings.json` (LayoutParserDecrypt:Path, Logging:File:Directory, Examples:Path). Premissa: em prod `DEPLOY_PATH = C:\inetpub\wwwroot\layoutparser`, API como serviço `LayoutParserApi` (deploy.yml), IIS não hospeda a API. ACL Full: SYSTEM + elson.lopes.
- **Porta**: appsettings fixa Kestrel em `0.0.0.0:5000` (vence o launchSettings — até `dotnet run` escuta 5000). Instância publicada deve usar override `Kestrel__Endpoints__Http__Url=http://localhost:5100`.
- API ainda **sem** `UseWindowsService` (spec pendente com @lp-backend-dev); execução manual: `dotnet LayoutParserApi.dll` na pasta api. Redis local existe (localhost:6379). IIS instalado e rodando, mas **sem** ANCM/Hosting Bundle.
- Máquina de PRODUÇÃO: WINSRV2022-LIB / 172.25.32.42 — nunca tocar a partir daqui.

Ver também [[security-regression-appsettings]].
