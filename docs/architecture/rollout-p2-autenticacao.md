# Rollout do P2 — ligar a autenticação sem derrubar o front

> `@lp-architect` (Aria), 2026-08-11. O código já existe: `ApiKeyGateFilter` (global, **fail-open**),
> `ApiKeyGatePolicy` (allowlist por segmento), `Security:ApiKey` / `Security:AnonymousPaths`. P2 **não é
> escrever o gate** — é ligá-lo numa ordem que não tranca o `LayoutParserReact`. A ordem errada dá
> **401 na aplicação inteira**.

## O fato que define a ordem

Verificado hoje: **o front NÃO manda `X-Api-Key`.** O interceptor axios (`api.ts:59-66`) só injeta
`X-Correlation-ID`. Então, no minuto em que `Security__ApiKey` for provisionada em produção, **toda
chamada do React que não estiver na allowlist toma 401** — e nada no front está na allowlist.

Por isso o gate nasce fail-open (documentado no próprio filtro): ligar fail-closed no primeiro deploy
"subiria a API segura e inútil". A ativação é uma sequência, não um commit.

## Sequência seseg — cada passo é inerte até o seguinte

| # | Passo | Repo/Dono | Por que é seguro nesta ordem |
|---|---|---|---|
| 1 | Front passa a **enviar** `X-Api-Key` **se** `VITE_API_KEY` estiver definida | React / `@lp-front-dev` | Gate ainda fail-open; header enviado é **ignorado**. Sem `VITE_API_KEY`, não envia nada. Zero efeito hoje, pré-requisito de tudo |
| 2 | Definir `Security:AnonymousPaths` = `["/health"]` (cobre `/health` e `/health/ready`) | API config / `@lp-devops` | O smoke test do deploy **precisa** alcançar `/health/ready` sem chave. Sem isso, ligar a chave quebra o próprio gate de deploy |
| 3 | Provisionar em **DEV**: `Security__ApiKey` no Environment do serviço + secret `API_KEY_DEV` no CI + `VITE_API_KEY` no build de dev do front | `@lp-devops` + `@lp-front-dev` | Valida ponta a ponta num ambiente descartável: front funciona, smoke test 200, 401 só sem header |
| 4 | Só então **PROD**: `Security__ApiKey` no Environment de produção + `API_KEY_PROD` + build de prod do front com a chave | `@lp-devops`, **aprovação do dono** | É mudança de segurança em produção. Passa pelo environment reviewer (quando ligado) e pela decisão do dono. Nunca antes de o passo 3 fechar verde |

**Regra de ouro:** a chave de produção só entra **depois** de o front de produção já estar enviando o
header e o dev ter validado. Inverter isso é o 401-geral.

## Duas ressalvas honestas — decisão do dono, não detalhe

1. **Sem TLS, a chave viaja em claro.** `UseHttpsRedirection` está comentado (`Program.cs`). Chave
   compartilhada sobre HTTP é *"sensação de segurança, não segurança"* (palavra do próprio filtro).
   Numa rede interna fechada pode ser um interino aceitável — mas é **decisão consciente**, não algo a
   silenciar. Recomendo TLS **junto ou antes** de habilitar em prod, ou registrar o risco aceito.
2. **Chave compartilhada é um degrau, não o destino.** Não identifica quem chamou nem permite revogar
   um consumidor. O alvo recomendado é **autenticação integrada Windows/Negotiate** — o ambiente é AD,
   então já suporta. P2 fecha o buraco de "API 100% anônima"; não é a arquitetura final de auth.

## ⛔ CORREÇÃO ESTRUTURAL (Aria, 2026-08-11, após o passo 1) — a premissa de 2 camadas estava errada

Escrevi esta spec assumindo **browser → API .NET** (2 camadas). **A realidade é 3 camadas**, e há um
esforço de autenticação paralelo que eu não tinha visto:

```
Browser  ──(Entra OIDC, sessão cifrada)──►  BFF Fastify (server/)  ──(proxy /api)──►  API .NET
```

- O `LayoutParserReact` tem um **BFF Node (Fastify)** em `server/`, e a branch `codex/feat-entra-oidc`
  está implementando **login via Microsoft Entra ID (Azure AD)** nele: `server/src/oidc.ts` (novo),
  `AuthenticationGate.tsx` (novo), session store cifrada, roles vindas de claims do token.
- O BFF já faz proxy de `/api` para o upstream (`app.ts:296`, `config.upstreamUrl`) e, no
  `rewriteProxyHeaders` (`app.ts:115`), **remove `authorization` e `cookie`** (a sessão do usuário
  nunca vaza para a API) e **injeta headers de identidade** (`trustedUserHeader`,
  `trustedRolesHeader`, `x-correlation-id`).

**O que isso quebra na minha spec:**

1. **O passo 1 está na camada errada.** O browser fala com o **BFF**, não com a API. Pôr a chave no
   bundle (`VITE_API_KEY`) **expõe o segredo compartilhado a quem abrir a SPA** e é redundante — a
   confiança do browser é a sessão Entra, não uma API key. A entrega do Remy está **correta como
   código e verde nos testes**, mas é **o design errado para esta arquitetura**. Fica **retida, não
   mergeada** (o Remy já a segurou por instinto certo — a árvore está na branch do OIDC).

2. **O contrato do salto BFF→API não está acordado.** Hoje o BFF manda `trustedUserHeader`/roles; a
   API (com o gate ligado) espera `X-Api-Key`. **Nenhum dos dois fala a língua do outro.** O
   `ApiKeyGateFilter` **tem lugar sim** — mas o `X-Api-Key` é injetado pelo **BFF** no
   `rewriteProxyHeaders`, server-side, **nunca pelo browser**.

### Arquitetura reconciliada (recomendação, decisão do dono)

Os dois esforços são **complementares, em camadas diferentes** — não concorrentes:

| Fronteira | Mecanismo | Responde |
|---|---|---|
| Browser ↔ BFF | **Entra OIDC** (branch `codex/feat-entra-oidc`) | *quem é o usuário* — identidade real, roles, revogação, sem segredo no browser |
| BFF ↔ API .NET | **rede** (a API só aceita conexão do BFF) **ou** `X-Api-Key` injetado pelo BFF | *esta chamada vem do nosso BFF confiável* |
| Dentro da API | consumir `trustedUserHeader`/roles para autorização e auditoria | *o quê este usuário pode fazer* — mecanismo que a API **ainda não tem** |

Entra OIDC **é o "destino recomendado"** que esta própria spec nomeou (auth integrada AD). A chave
compartilhada deixa de ser a defesa principal e vira, no máximo, o cinto do salto BFF→API.

### O que fica PARADO até o dono decidir a fronteira BFF↔API

- **Não** mergear o passo 1 do Remy como está (chave no browser).
- **Não** provisionar `Security__ApiKey` para o browser usar.
- A pergunta para o dono: o salto BFF→API é protegido por **rede** (mais simples, a API só escuta o
  BFF) ou por **`X-Api-Key` injetado no BFF** (defesa em profundidade, mas mais uma chave para girar)?
  E: a API deve passar a **consumir a identidade** (`trustedUserHeader`) para autorização por papel,
  ou o BFF já basta como porteiro nesta fase?

Até isso, o P2 "chave compartilhada" está **suspenso** — não porque falhou, mas porque a arquitetura
real é melhor e já está sendo construída noutra frente.

---

## DECISÃO DO DONO (2026-08-11) e o plano reconciliado

O dono escolheu: **(1)** fronteira BFF↔API protegida **por rede** (a API só aceita o BFF; sem
`X-Api-Key` nesse salto); **(2)** a API **passa a consumir identidade** (`x-iis-user`/`x-iis-roles`)
para autorização por papel e auditoria.

**Contrato de header (lido do BFF, `server/src/config.ts:289-298`):**
- usuário: **`x-iis-user`** (env `BFF_TRUSTED_USER_HEADER`)
- papéis: **`x-iis-roles`** (env `BFF_TRUSTED_ROLES_HEADER`), CSV
- O BFF **remove** as versões *inbound* desses headers antes de injetar (`app.ts:122-123`) —
  anti-spoofing na camada dele. Isso protege o caminho browser→BFF. **Não** protege o caminho
  direto-para-a-API (ver acoplamento letal abaixo).

### 🔴 ACOPLAMENTO LETAL — a decisão tem um pré-requisito P0 que NÃO está satisfeito hoje

`Program.cs:684` e `appsettings.json:142`: a API escuta em **`http://0.0.0.0:5000`** — **todas as
interfaces, toda a rede**. Provado nesta sessão: bati em `172.25.32.42:5000` direto, várias vezes,
sem BFF.

Se a API passar a **confiar** em `x-iis-user`/`x-iis-roles` **enquanto** estiver em `0.0.0.0:5000`,
então **qualquer um na rede** manda `x-iis-user: admin` + `x-iis-roles: admin` direto na `:5000` e
**vira admin** — sem Entra, sem BFF, sem sessão. O stripping do BFF não ajuda: você simplesmente
**pula o BFF**. Header de confiança só é confiável se a origem for garantida — e hoje não é.

**Portanto a ordem é inegociável:**

1. **O BFF vira a única porta de produção** (a branch `codex/feat-entra-oidc` em produção; o painel
   React passa a falar com o BFF, não mais com a `:5000` direto — hoje ele fala direto, per memória).
2. **Trancar a rede:** a API deixa de escutar em `0.0.0.0`. Se BFF e API são **co-hospedados** →
   bindar em `127.0.0.1:5000` (via `Kestrel__Endpoints__Http__Url`, o canal que o deploy já usa). Se
   **hosts diferentes** → firewall na `:5000` liberando **só o IP do BFF**. Topologia a confirmar no
   host (`@lp-devops`).
3. **Só então** a API consome `x-iis-user`/`x-iis-roles` para autorização e auditoria (`@lp-backend-dev`).

Inverter isso é publicar a vulnerabilidade. **Nada de (3) antes de (2) verificado**, e **(2) não pode
vir antes de (1)** sem derrubar o painel atual, que ainda bate direto na `:5000`.

### Dependências e o que fica parado

- A frente inteira depende do **BFF/OIDC (Codex) chegar em produção como porta única**. Enquanto o
  painel falar direto com a `:5000`, não dá para trancar a rede.
- **`ApiKeyGateFilter` não é mais o mecanismo escolhido** (rede venceu). Ele é fail-open, então é
  inofensivo, mas é meia-segurança que engana quem lê — candidato a **remoção** como higiene
  (`@lp-backend-dev`, baixa prioridade).
- **A mudança do Remy (chave no browser) é descartada** — design errado para esta arquitetura. Os 4
  arquivos precisam ser **revertidos** para não entrarem por engano na branch do OIDC.

### RESULTADO parcial (2026-08-11) — consumo de identidade CONSTRUÍDO e verificado

Branch `feat/identidade-do-bff` (de `develop`), commit `c7489ca`. Entregue pelo `@lp-backend-dev`,
**verificado por mim** contra o código e o teste:

- **`TrustedIdentityMiddleware`** lê `x-iis-user`/`x-iis-roles` e popula `ICurrentUser` +
  `HttpContext.User`. Nomes de header configuráveis (`Security__TrustedUserHeader`, default
  `x-iis-user`).
- **Guarda de loopback ATIVA e provada.** Confirmei o teste `Origem_nao_loopback_ignora_os_headers`:
  manda `x-iis-user: admin` de `172.25.32.42` e afirma identidade **anônima** nos dois slots. É real,
  não vacuoso — fecha a forja de identidade **mesmo com a API em `0.0.0.0`**. Default
  `TrustIdentityFromLoopbackOnly=true`, deliberadamente fora do `appsettings.json` (sem booleano
  tentador).
- **Auditoria** (`AuditActionFilter`) passa a gravar o usuário (`Name` ou `anon`).
- **`ApiKeyGateFilter`/`ApiKeyGatePolicy` REMOVIDOS** — rede + identidade venceram; `Security:ApiKey`
  e `Security:AnonymousPaths` saíram do `appsettings.json`.
- `dotnet build` verde, 294 testes, mutação da guarda derruba a suíte em 3 pontos.

**Sem `[Authorize]` em endpoint** — enforcement por papel fica para a decisão de produto (abaixo).

### O que ainda falta

1. **Trava de rede (`127.0.0.1`)** — `@lp-devops`. É a 2ª camada (a guarda de loopback já é a 1ª).
   **Gated:** só vai a produção depois de confirmar que o painel de produção passa pelo BFF (senão
   quebra o acesso direto à `:5000`).
2. **Enforcement por papel** — decisão de produto do dono: quais endpoints viram privilegiados. O
   mecanismo (`ICurrentUser.IsInRole`) já está pronto. Candidatos que o `@lp-backend-dev` levantou:
   `DataGenerationController`, `execute-candidates`/transformações que sobem `.exe`, limpeza de cache
   (privilegiado); `GET api/logs` (admin-only, expõe internals); `ParseController/upload` (operador).
3. **Higiene de CI** — `@lp-devops`: `ci-dev.yml` (~519-524, 588) e `deploy.yml` (~919) ainda injetam
   `Security__ApiKey` (agora morto — no-op). Limpar. O secret `API_KEY_DEV`/`VITE_API_KEY` do rollout
   antigo perde a função.
4. **Doc** — `@lp-doc`: README documenta o `ApiKeyGateFilter` e a chave compartilhada; atualizar para
   identidade-do-BFF + guarda de loopback.

### O que NÃO estou despachando agora, e por quê

Não vou mandar o `@lp-backend-dev` construir o consumo de identidade ainda: contra uma API em
`0.0.0.0`, isso é **construir a vulnerabilidade**. E não vou mandar o `@lp-devops` trancar a rede
ainda: quebraria o painel atual, que fala direto com a `:5000`. As duas coisas **entram em lockstep
com o BFF virar porta única** — trabalho da branch do Codex. O que faço agora é **sequenciar e
registrar**; a construção .NET entra quando o BFF estiver pronto para ir a produção.

---

## AUDITORIA LOCAL (2026-08-12) e decisão de enforcement por papel

Rodei a API localmente (`dotnet run`, `TrustIdentityFromLoopbackOnly=true` default, requisições de
`127.0.0.1`) e bati nos endpoints candidatos com `x-iis-user`/`x-iis-roles` simulados, para decidir
com log em mãos — não só por inspeção de código.

**O que os logs mostraram:**

- `POST /api/parse/upload` (tem `[ServiceFilter(typeof(AuditActionFilter))]`): identidade flui
  ponta a ponta. Upload multipart válido com `x-iis-user: admin.teste` produziu
  `AUDIT | UserId:admin.teste | ... | Action:Executed`; sem headers, produziu `UserId:anon`. Confirma
  que o mecanismo funciona quando o filtro está presente.
- **Achado real, não previsto no plano:** uma requisição que falha o `ModelState` (ex.: `multipart`
  malformado, 400 do `[ApiController]`) **não gera nenhuma linha `AUDIT`** — o filtro automático de
  validação do `[ApiController]` roda antes e curto-circuita o pipeline sem chamar
  `AuditActionFilter.OnActionExecuting`. Ou seja, **hoje a auditoria só cobre requisições
  bem-formadas**; tentativas malformadas (que é justamente o tipo de tráfego que se quer auditar em
  um cenário de abuso) passam sem registro.
- **`AuditActionFilter` só está aplicado no `ParseController`.** Bati em `GET /api/logs` (anônimo e
  como admin, ambos 200) e em `POST /api/DataGeneration/generate-synthetic` — **zero linhas `AUDIT`**
  em qualquer um. O middleware de identidade roda globalmente (então `ICurrentUser` está populado),
  mas nada grava quem chamou esses endpoints hoje. Os "candidatos" listados no plano original
  (`DataGenerationController`, `execute-candidates`, limpeza de cache, `GET api/logs`,
  `ParseController/upload`) têm **cobertura de auditoria desigual**: só o último é realmente auditado.

**Decisão de enforcement por papel** (mecanismo `ICurrentUser.IsInRole`, ainda sem `[Authorize]` em
nenhum endpoint):

| Endpoint | Papel exigido | Por quê |
|---|---|---|
| `GET /api/logs` | `admin` | Expõe internals (stack traces, paths de servidor) — maior risco de vazamento, zero fricção de uso legítimo (só operação de suporte) |
| `POST /api/DataGeneration/*` (`generate-synthetic`, `generate-synthetic-zip`, `process-excel`) | `admin` | Gera dados sintéticos em volume / roda IA — custo computacional e superfície de abuso, uso não é operação de rotina |
| `POST /api/TransformationExecution/execute-candidates`, `/execute-lowcode` | `admin` | Sobe o runner LowCode (`.exe`) — mesma classe de risco do P0/P1 já fechado (execução de processo externo) |
| `POST /api/MapperDatabaseController/refresh-cache` | `operador` | Ação de manutenção, não deveria ser self-service anônimo, mas não é tão sensível quanto executar transformação |
| `POST /api/parse/upload` | **nenhum (mantém aberto)** | É o caminho operacional principal do sistema; já tem auditoria real. Fechar aqui quebraria o uso normal sem ganho de segurança proporcional — o registro por `UserId`/`anon` já dá rastreabilidade |

**Pré-requisito antes de qualquer `[Authorize]` entrar em vigor — 2 gaps que o `[Authorize]`
sozinho NÃO resolve:**

1. **Aplicar `[ServiceFilter(typeof(AuditActionFilter))]`** em `LogsController`,
   `DataGenerationController` e `TransformationExecutionController` — hoje eles não são auditados,
   então mesmo com `[Authorize]` ligado não há rastro de quem tentou (e falhou) acessar.
2. **Corrigir a ordem de filtros** para que `AuditActionFilter` rode mesmo quando o `ModelState` é
   inválido (ou aceitar formalmente que auditoria = "requisição bem-formada que chegou à action" —
   mas isso precisa ser uma decisão registrada, não um acidente de ordering).

Sem os dois, `[Authorize]` fecha o acesso mas não deixa rastro de quem tentou contornar — é
enforcement sem visibilidade, metade da defesa. **Trabalho de `@lp-backend-dev`**, antes de aplicar os
atributos da tabela acima.

---

## (Histórico) O que eu havia disparado — antes da correção acima

**Só o passo 1** (front enviar o header condicionalmente). Entregue e verde, mas **retido** pela
correção estrutural acima — era o design de 2 camadas.
