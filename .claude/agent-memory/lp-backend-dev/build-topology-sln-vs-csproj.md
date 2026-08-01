---
name: build-topology-sln-vs-csproj
description: Na raiz do repo há .sln E .csproj; `dotnet build` sem argumento resolve pela SOLUTION — e é isso que o deploy.yml de produção executa. Adicionar projeto à .sln muda o que o deploy compila.
metadata:
  type: project
---

`dotnet build` sem argumento na raiz do `LayoutParserApi` resolve pelo **`LayoutParserApi.sln`**,
não pelo `.csproj` (confirmado por execução: `dotnet build -getProperty:X` devolve
`MSB1063: Cannot access properties or items when building solution files`).

Isso importa porque os dois workflows tratam o build de formas diferentes:

- `.github/workflows/ci-dev.yml` builda o **`$API_PROJECT`** explicitamente → imune à .sln.
- `.github/workflows/deploy.yml` (produção) roda **`dotnet restore` + `dotnet build` sem
  argumento** dentro do diretório da API → passa pela .sln.

**Why:** por isso o projeto de testes (`tests/LayoutParserApi.Tests`, criado em 2026-07-31) ficou
**fora da .sln** de propósito: incluí-lo faria o deploy de produção restaurar xUnit e compilar
testes, e uma falha de restore ali quebraria um deploy que não tem nada a ver com teste. Rodar
localmente: `dotnet test tests\LayoutParserApi.Tests\LayoutParserApi.Tests.csproj`.

**How to apply:** ao criar qualquer projeto novo aninhado nesta árvore, (1) adicione o caminho ao
`DefaultItemExcludes` do `LayoutParserApi.csproj` (senão os `.cs` entram no glob da API — já
aconteceu com `mcp/`), e (2) trate entrar na .sln como decisão de `@lp-devops`, não como detalhe.
Ver também [[nuget-private-feed-401]] — o restore de xunit 2.9.3 pelo nuget.org funcionou normal
pelo Windows nesta máquina.
