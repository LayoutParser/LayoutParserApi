---
name: nuget-private-feed-401
description: Restore de pacote NuGet novo falha (NU1301 401) por causa do feed privado da org; workaround = nuget.config só com nuget.org
metadata:
  type: project
---

Adicionar um `PackageReference` novo e rodar `dotnet restore`/`build` falha com
`NU1301 ... 401 (Unauthorized)` na fonte **CENTRAL TFS**
(`https://tfs.ndd.tech/NDD-DECollection/.../nddCentralSolucoesPackages`). Uma única
fonte com 401 aborta o restore inteiro, mesmo que o pacote exista no nuget.org e já
esteja no cache global (`~/.nuget/packages`).

**Why:** o ambiente não tem credenciais para o feed privado da organização (registrado
globalmente em `dotnet nuget list source`). O restore consulta todas as fontes e a que
retorna 401 derruba tudo.

**How to apply:** ao introduzir uma dependência NuGet em qualquer projeto do repo, crie
um `nuget.config` **escopado na pasta do projeto** com `<clear/>` + só o nuget.org
(feito em `ai/XslSynth/nuget.config`). Isso torna o `dotnet build` reprodutível sem o
flag `-s`. Alternativa pontual: `dotnet restore -s https://api.nuget.org/v3/index.json`.
`nuget.config` não é segredo, mas é config — avise `@lp-devops` que foi adicionado.
Confirmado em 2026-07-10 ao adicionar `DocumentFormat.OpenXml` (v3.3.0, já no cache).
