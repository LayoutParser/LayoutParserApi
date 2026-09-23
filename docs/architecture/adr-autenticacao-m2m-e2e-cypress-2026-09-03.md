# ADR — Autenticação machine-to-machine para a suíte E2E Cypress

> `@lp-architect` (Aria), 2026-09-03. Cobre a Frente A da issue #221 (epic) e a issue #218 (gate).
> Decisão de arquitetura — **não implementada aqui**. Execução: `@lp-backend-dev`.

## Status

Proposto. Aguarda confirmação do dono antes de `@lp-backend-dev` implementar.

## Contexto

### O que já existe (verificado no código, não por suposição)

A cadeia de identidade da API hoje é 100% derivada de headers confiáveis injetados pelo BFF
(`LayoutParserReact/server`), sob uma guarda de rede:

```
Browser ──(Entra OIDC)──► BFF Fastify ──(injeta x-iis-user/x-iis-roles)──► API .NET
```

- `Services/Security/TrustedIdentityMiddleware.cs`: só confia nos headers `x-iis-user`/`x-iis-roles`
  se `context.Connection.RemoteIpAddress` for **loopback** (`TrustIdentityFromLoopbackOnly`, default
  `true`, deliberadamente fora do `appsettings.json`). Fora de loopback → identidade **anônima**,
  sempre, sem exceção configurável hoje.
- `Services/Security/TrustedHeaderAuthenticationHandler.cs`: não autentica nada por conta própria —
  só formaliza para o `AuthorizationMiddleware` o que o `TrustedIdentityMiddleware` já populou em
  `HttpContext.User`. É o único `AuthenticationScheme` registrado (`Program.cs:191-194`).
- `[Authorize]`/`[Authorize(Roles=...)]` (issue #32, reaberto parcialmente pela #93) está em
  produção em vários controllers.
- O antigo `ApiKeyGateFilter`/`Security:ApiKey` (chave compartilhada, global, fail-open) foi
  **removido deliberadamente** (`feat/identidade-do-bff`, commit `c7489ca`) — não deve ser
  reintroduzido (`.claude/rules/security.md`).

### Achado que corrige a premissa da epic #221

Auditei os 3 endpoints citados no critério de aceite da #221 diretamente no código
(`Controllers/TransformationExecutionController.cs`, `Controllers/AutoTransformationController.cs`):

| Endpoint | `[Authorize]` hoje? | Observação |
|---|---|---|
| `POST /api/TransformationExecution/execute-lowcode` | **Sim** (`[Authorize]`, linha 1251 — sem `Roles`, qualquer autenticado) | É o único dos 3 que de fato bloqueia sem identidade. Bate com o 401 relatado na #218. |
| `POST /api/transformation-execution/execute` | **Não** (linha 115, sem atributo) | Endpoint anônimo hoje. Não deveria dar 401 por falta de auth — se o Cypress viu 401 aqui, não é este mecanismo (investigar à parte, fora do escopo deste ADR). |
| `POST /api/AutoTransformation/generate-for-layout` | **Não** (linha 71, sem atributo) | Confirma a suspeita da própria epic: o erro relatado na #219 (`"Tipo de layout não suportado: 2"`, HTTP 200) é de **cadastro/validação de tipo de layout**, não de autenticação. Nada aqui depende do mecanismo M2M. |

**Consequência prática:** o mecanismo M2M desta ADR só precisa desbloquear com certeza
`execute-lowcode` hoje. Ainda assim, desenho o mecanismo de forma genérica (qualquer endpoint
`[Authorize]` futuro) porque a tabela de enforcement por papel em
`docs/architecture/rollout-p2-autenticacao.md` já lista mais candidatos (`execute-candidates`,
`DataGeneration/*`, `GET /api/logs`) que podem ganhar `[Authorize]` a qualquer momento — o
Cypress vai precisar do mesmo mecanismo para eles.

### A restrição que define o desenho

O Cypress (`LayoutParserCypress`) bate **direto** na API (`http://172.19.176.1:5100` visto do
WSL, ou equivalente em CI) — **não passa pelo BFF**. Isso significa que a origem da requisição
**não é loopback** por definição. O mecanismo de headers confiáveis do BFF (`x-iis-user`) é,
por desenho, inaplicável aqui — não porque falte configuração, mas porque a guarda de loopback
existe exatamente para rejeitar essa origem quando ela não é o BFF legítimo.

Logo, "autenticar o Cypress" não pode ser "convencer `TrustedIdentityMiddleware` a confiar em
mais uma origem" — isso reabriria o vetor que a guarda de loopback foi desenhada para fechar
(qualquer host da rede forjando `x-iis-user: admin`). Precisa ser um **mecanismo paralelo**,
com seu próprio segredo e seu próprio raio de confiança, nunca ativo em produção.

## Opções avaliadas

### Opção A — OAuth2 Client Credentials Grant via Entra ID

Registrar um App Registration "de serviço" no mesmo tenant Entra que o BFF já usa para OIDC.
O Cypress obtém um token via `client_id`/`client_secret` (grant `client_credentials`, sem
usuário/browser), manda `Authorization: Bearer <token>` direto na API. A API adiciona um
**segundo** `AuthenticationScheme` (JWT Bearer, `Microsoft.Identity.Web` ou
`Microsoft.AspNetCore.Authentication.JwtBearer`) que valida issuer/audience/assinatura contra o
tenant Entra, populando `HttpContext.User` com uma claim de "app" (não de usuário humano).

**Prós:**
- Identidade real, revogável no Entra sem tocar em código/config da API (desabilitar o App
  Registration mata o acesso na hora).
- Mesmo padrão de confiança que o BFF já usa para OIDC — não introduz um segundo modelo mental
  de "quem eu confio e por quê".
- Funciona igual em qualquer rede (não depende de loopback nem de "ambiente == dev") — se um dia
  o Cypress rodar contra staging atrás de rede real, continua funcionando sem redesenho.

**Contras:**
- Adiciona uma segunda stack de autenticação inteira (pacote NuGet novo, configuração de
  authority/audience, cache de JWKS) para resolver um problema hoje limitado a 1 endpoint gated.
  Custo de implementação e manutenção desproporcional ao problema atual.
- **Reabre a pergunta de rede que o `rollout-p2-autenticacao.md` já resolveu na direção
  oposta**: a API está a caminho de escutar só em `127.0.0.1` (BFF co-hospedado). Um JWT Bearer
  que precisa ser alcançável pelo runner do Cypress (seja ele CI ou uma workstation) exige que a
  API **aceite conexão de fora do loopback** — ou seja, ou o Cypress passa a rodar co-hospedado
  também (não é o caso hoje, roda do WSL/CI), ou a trava de rede planejada ganha uma exceção
  específica para o IP do runner, que é mais superfície para manter do que o problema original.
- Gestão de segredo (`client_secret`) do App Registration em CI é outro segredo rotacionável a
  cuidar — mesma classe de trabalho que o projeto já está tentando reduzir (ver
  `.claude/rules/security.md`, pendência de segredos).
- Overkill de escopo: o Entra tenant é da NDD inteira (mesmo raciocínio do "credencial
  compartilhada por ~231.890 times" documentado para a senha SQL) — criar um App Registration
  novo pode exigir aprovação de time de identidade/AD fora do controle deste projeto, com prazo
  não previsível.

### Opção B — Identidade de serviço sintética, aceita só fora de produção (RECOMENDADA)

Um mecanismo **novo e paralelo** ao `TrustedIdentityMiddleware`, que nunca reaproveita a guarda
de loopback nem os headers `x-iis-*`. Um middleware dedicado
(`ServiceCredentialAuthenticationMiddleware` ou equivalente) reconhece um par de headers
próprios — por exemplo `X-Service-Credential` (segredo) + `X-Service-Name` (identificador fixo,
ex.: `cypress-e2e`) — e, se baterem contra um segredo configurado
(`Security:ServiceCredential:Secret`, via `dotnet user-secrets`/env var, nunca no
`appsettings.json`), popula `HttpContext.User` com uma identidade sintética própria: nome fixo
(`svc-cypress-e2e`), role dedicada (`servico-e2e`) — **não** `admin`, **não** reaproveita papéis
humanos.

**A trava que impede isso de virar o próximo `ApiKeyGateFilter` (chave global fail-open):**

1. **Guarda de ambiente, não só de config.** O middleware recusa mesmo com segredo configurado
   e correto se `IWebHostEnvironment.IsProduction() == true` — checagem **hard-coded no código**,
   não um booleano em `appsettings.json` (mesmo padrão de "sem booleano tentador" que
   `TrustIdentityFromLoopbackOnly` já usa). Em produção, o middleware é um no-op independente do
   que estiver configurado.
2. **Escopo de papel mínimo.** `servico-e2e` só é suficiente para o que `[Authorize]` (sem
   `Roles`) já exige hoje (`execute-lowcode`). Se um endpoint futuro exigir `[Authorize(Roles =
   "admin")]`, essa identidade sintética **não** passa — precisa de decisão explícita nova para
   ganhar mais papel, não herda automaticamente.
3. **Nunca no browser.** O segredo vive só no ambiente do runner Cypress/CI (GitHub Actions
   secret, nunca em `VITE_*`/bundle do front) — corta o vetor que matou o `ApiKeyGateFilter`
   anterior (chave vazando pra qualquer um que abrisse a SPA).
4. **Auditoria distinta.** `AuditActionFilter`/logs gravam `svc-cypress-e2e` como usuário — nunca
   se mistura com tráfego humano nem cai no bucket anônimo; dá pra filtrar/alarmar se aparecer
   fora do padrão esperado (ex.: fora do horário de CI).
5. **Ordem no pipeline:** roda **depois** do `TrustedIdentityMiddleware` e só age se este deixou
   a identidade anônima — nunca sobrescreve uma identidade real vinda do BFF.

**Prós:**
- Não toca na guarda de loopback nem no modelo de confiança do BFF — zero risco de regressão na
  postura de produção (a trava por ambiente é um segundo cinto, independente do segredo).
- Implementação pequena: um middleware + uma option class + 1 header check, no mesmo estilo do
  `TrustedIdentityMiddleware` que já existe — sem pacote novo, sem tenant Entra novo, sem
  aprovação externa.
- Resolve o problema real e único confirmado nesta auditoria (`execute-lowcode`) sem
  sobre-engenharia.

**Contras:**
- É um segredo compartilhado (mesma classe de risco operacional que qualquer shared secret):
  precisa de rotação e de nunca vazar para logs. Mitigado pela trava de ambiente — mesmo se
  vazado, é inofensivo em produção.
- Não dá identidade "real" (não é um usuário/app auditável no Entra) — para fins de
  compliance/rastreabilidade organizacional, é mais fraco que a Opção A. Aceitável porque o
  consumidor é só a suíte E2E interna, não uma integração de terceiro.
- Se amanhã o Cypress precisar rodar contra produção (não é o caso hoje — os gates rodam contra
  dev), este mecanismo **não serve** por desenho (é isso que a trava de ambiente garante) — nesse
  cenário, migrar para a Opção A vira necessário, não opcional.

## Recomendação

**Opção B agora.** Resolve o bloqueio real e único confirmado (`execute-lowcode`), sem reabrir a
pergunta de rede que `rollout-p2-autenticacao.md` já está fechando na direção oposta (API rumo a
`127.0.0.1`), sem depender de aprovação externa de time de identidade/AD, e sem adicionar uma
segunda stack de autenticação para 1 endpoint.

**Opção A fica registrada como estado-alvo condicional**, não descartada: se o escopo mudar para
"Cypress precisa rodar contra staging/produção real" ou "múltiplas integrações M2M externas
precisam de identidade revogável e auditável no Entra", a Opção B não escala — nesse momento,
reabrir este ADR e migrar. Não implementar a Opção A preventivamente agora.

## Plano de implementação (para `@lp-backend-dev`)

1. **Options class** `ServiceCredentialOptions` (`Services/Security/`), seção
   `Security:ServiceCredential` — campos `HeaderName` (default `X-Service-Credential`),
   `ServiceNameHeader` (default `X-Service-Name`), `Secret` (vazio por default, nunca no
   `appsettings.json` versionado — só `user-secrets`/env var `Security__ServiceCredential__Secret`).
2. **Middleware** `ServiceCredentialAuthenticationMiddleware`, registrado em `Program.cs`
   **depois** de `app.UseMiddleware<TrustedIdentityMiddleware>()` e **antes** de
   `app.UseAuthentication()`:
   - Se `env.IsProduction()` → `return await _next(context)` sem checar nada (guarda hard-coded).
   - Se `HttpContext.User` já autenticado (veio do BFF) → não sobrescreve, segue.
   - Senão, compara o header `Secret` via comparação de tempo constante
     (`CryptographicOperations.FixedTimeEquals`, não `==`) contra o valor configurado; se vazio
     no config, middleware é sempre no-op (evita "secret vazio bate com header vazio").
   - Se bater, popula `HttpContext.User` com `ClaimTypes.Name = "svc-" + serviceName`,
     `ClaimTypes.Role = "servico-e2e"`, `authenticationType: "ServiceCredential"` — mesmo padrão
     de `ConstruirPrincipal` do `TrustedIdentityMiddleware`, sem duplicar lógica (extrair helper
     compartilhado se fizer sentido).
   - Nunca lança exceção — mesmo princípio de resiliência do resto do projeto
     (`.claude/rules/dotnet-standards.md` §Resiliência): erro de config aqui degrada para
     anônimo, não derruba a app.
3. **Sem novo `AuthenticationScheme`.** `TrustedHeaderAuthenticationHandler` já só lê
   `HttpContext.User` — reaproveitar, não duplicar.
4. **Auditoria:** confirmar que `execute-lowcode` (e qualquer endpoint `[Authorize]` que o
   Cypress precise chamar) tem `[ServiceFilter(typeof(AuditActionFilter))]` — se não tiver, é o
   mesmo gap já registrado em `rollout-p2-autenticacao.md` (auditoria desigual entre
   controllers). Não bloqueia este ADR, mas registrar achado se for encontrado.
5. **CI/Cypress (fora deste repo, para `@lp-devops`/dono do `LayoutParserCypress` coordenarem):**
   - Gerar um segredo forte, guardar como `Security__ServiceCredential__Secret` no
     `Environment` do serviço de **dev** (nunca produção) e como secret do GitHub Actions
     (`E2E_SERVICE_CREDENTIAL` ou nome equivalente) no repo `LayoutParserCypress`.
   - Cypress passa a mandar `X-Service-Credential: <segredo>` + `X-Service-Name: cypress-e2e` em
     todo `cy.request()` que hoje toma 401.
6. **Teste automatizado** (mesmo estilo do teste de mutação da guarda de loopback já existente):
   confirmar que, com `ASPNETCORE_ENVIRONMENT=Production`, o header correto **não** autentica —
   este é o teste que protege a garantia central deste ADR.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Segredo do Cypress vaza (log, commit acidental, CI mal configurado) | Trava de ambiente torna o vazamento inofensivo em produção; em dev, o dano é limitado a um role `servico-e2e` de baixo privilégio, não `admin`. Rotação simples (é só um secret de CI, sem consumidor externo à NDD). |
| Alguém reintroduz a checagem de ambiente como config em vez de hard-code, e um deploy de produção herda `ASPNETCORE_ENVIRONMENT` errado | Mesma classe de risco que já existe hoje para `TrustIdentityFromLoopbackOnly` — mitigar com o teste do item 6 do plano, que já teria pego essa classe de regressão antes do merge. |
| Escopo cresce informalmente ("já que temos o mecanismo, usa pra mais coisa") e o role `servico-e2e` vira um `admin` disfarçado ao longo do tempo | Este ADR fixa que qualquer ampliação de papel exige decisão nova registrada, não só um PR de código — sinalizar explicitamente se `@lp-backend-dev` receber esse pedido futuramente. |
| Confundir esta credencial com o `ApiKeyGateFilter` removido e reintroduzir o padrão antigo (global, fail-open, exposto ao browser) | Diferenças documentadas explicitamente na seção da Opção B — nunca no bundle do front, sempre role mínimo, sempre travado por ambiente. |
| Opção B não escalar se o escopo do Cypress mudar (rodar contra prod/staging real) | Documentado como limite conhecido — não é surpresa futura, é a condição de gatilho para reabrir este ADR e migrar para a Opção A. |

## Nota sobre #219 — frente separada, não depende deste ADR

`generate-for-layout` não tem `[Authorize]` hoje; o erro reportado
(`"Tipo de layout não suportado: 2"`, HTTP 200 com `success:false`) é de validação de
`layoutType` do layout FIAT (`LAY_TXT_MQSERIES_ENVNFE_4.00_NFe`), não de autenticação. Nenhuma
parte do mecanismo M2M desta ADR afeta essa investigação — ela segue com `@lp-parser-llm`
(domínio de geração TCL/XSL), como a própria epic #221 já havia separado corretamente em
"Frente B". Confirmar via código/cadastro se `layoutType=2` deveria ser suportado pelo endpoint
(lacuna real) ou se é o cadastro do layout que está errado no banco.
