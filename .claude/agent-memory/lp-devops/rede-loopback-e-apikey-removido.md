---
name: rede-loopback-e-apikey-removido
description: API_KEY_DEV/PROD removidos de ci-dev.yml e deploy.yml (ApiKeyGateFilter morto); Kestrel bind mudou de 0.0.0.0 para 127.0.0.1 em appsettings.json/Program.cs/deploy.yml (canal Kestrel__Endpoints__Http__Url)
metadata:
  type: project
---

Em 2026-08-12 (branch `feat/identidade-do-bff`, commit local `4c0fc1d`, ainda sem push), executei
as duas pendências registradas em `docs/architecture/rollout-p2-autenticacao.md` ("O que ainda
falta", itens 1 e 3):

**Higiene de CI:** `Security__ApiKey`/`API_KEY_DEV` (`ci-dev.yml`) e `API_KEY_PROD` (`deploy.yml`)
removidos — eram código morto desde que `ApiKeyGateFilter`/`ApiKeyGatePolicy` saíram do código em
`c7489ca`. Os secrets `API_KEY_DEV`/`API_KEY_PROD`/`VITE_API_KEY` ficam sem consumidor (não apaguei
do GitHub — fora do escopo de terminal).

**Trava de rede:** mudei o bind de `http://0.0.0.0:5000` para `http://127.0.0.1:5000` em três
lugares: `appsettings.json` (default do repo), `Program.cs` (fallback do log de startup, mesma
constante), e — o canal que **realmente** chega em produção — adicionei
`Kestrel__Endpoints__Http__Url=http://127.0.0.1:5000` ao `$managed` do step "Configurar ambiente do
servico (upsert, nao destrutivo)" em `deploy.yml`. Motivo de usar o env var em vez de só editar o
JSON: o deploy de produção copia com `-Exclude appsettings.json` por padrão (preserva o arquivo do
destino) — o valor do repo só chega lá se `MIGRATE_CONFIG_TO_REPO=true` já tiver rodado, o que **não
está garantido**. Env var no Environment do serviço é o único canal que sempre chega (mesmo padrão
já usado para `LowCode__Package`, `ML__LowCodeTransformationsPath` etc — ver
[[github-protections-pending]] pro estado geral de gates não-enforced).

**Suposição que assumi (não 100% verificada por mim):** BFF e API são co-hospedados no mesmo
Windows Server 2022 (`172.25.32.42`/`BRNDDAPPBLD01`) — baseado em `deploy-production-topology`
(memória de projeto: front antigo já era servido via IIS nesse mesmo host, `DEPLOY_PATH` raiz
`C:\inetpub\wwwroot\layoutparser\`) e no fato de o smoke test de `deploy.yml` já rodar em
`localhost:5000` num runner self-hosted **dentro** desse host. Não confirmei o deploy.yml do repo
`LayoutParserReact` (BFF) para ver se ele aponta pro mesmo `DEPLOY_PATH`/host — isso é o único elo
que falta pra fechar 100%. Se algum dia aparecer evidência de que BFF e API rodam em hosts
diferentes, a trava correta vira firewall liberando só o IP do BFF na `:5000`, não bind em loopback
— reverter o `Kestrel__Endpoints__Http__Url` pra `0.0.0.0` nesse caso e abrir chamado de firewall.

**Build:** `dotnet build` deu 0 erros compilando pra um output dir separado (`-o` alternativo) —
o build normal falhou por lock de arquivo (uma instância local `LayoutParserApi.exe`, PID já rodando
na máquina, prendia `bin\Debug\net10.0\LayoutParserApi.dll/.exe`), não por erro de compilação. Não
matei o processo sem perguntar.

**Pendência que sobrou (não fiz, fora do escopo desta missão):** item 2 do "O que ainda falta"
(enforcement por papel / `[Authorize]`) é decisão de produto do dono, não deste commit.
