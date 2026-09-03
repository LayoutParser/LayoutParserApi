# ADR — Autenticação machine-to-machine para a suíte E2E Cypress

> `@lp-architect` (Aria), 2026-09-03. Cobre a Frente A da issue #221 (epic) e a issue #218 (gate).
> Decisão de arquitetura — **não implementada aqui**. Execução: `@lp-backend-dev`.
>
> **Revisão 2026-09-03 (mesmo dia):** decisão trocada de Opção B → **Opção A**, a pedido explícito
> do dono. Ver seção "Revisão da decisão" abaixo. Seções originais preservadas (raciocínio da
> Opção B continua válido como registro — só o veredito final mudou).

## Status

**Aceito — Opção A (client credentials via Entra).** Aguarda `@lp-backend-dev` implementar,
mediante aprovação final do dono no plano de implementação revisado.

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

### Opção A — OAuth2 Client Credentials Grant via Entra ID *(ESCOLHIDA — ver Revisão)*

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
  Custo de implementação e manutenção desproporcional ao problema **atual** (mas ver Revisão —
  o problema deixou de ser só o atual).
- **Reabre a pergunta de rede que o `rollout-p2-autenticacao.md` já resolveu na direção
  oposta**: a API está a caminho de escutar só em `127.0.0.1` (BFF co-hospedado). Um JWT Bearer
  que precisa ser alcançável pelo runner do Cypress (seja ele CI ou uma workstation) exige que a
  API **aceite conexão de fora do loopback** — ou seja, ou o Cypress passa a rodar co-hospedado
  também (não é o caso hoje, roda do WSL/CI), ou a trava de rede planejada ganha uma exceção
  específica para o IP do runner, que é mais superfície para manter do que o problema original.
  **Tratamento detalhado na seção "Rede: 127.0.0.1 vs client credentials" abaixo.**
- Gestão de segredo (`client_secret`) do App Registration em CI é outro segredo rotacionável a
  cuidar — mesma classe de trabalho que o projeto já está tentando reduzir (ver
  `.claude/rules/security.md`, pendência de segredos).
- Overkill de escopo: o Entra tenant é da NDD inteira (mesmo raciocínio do "credencial
  compartilhada por ~231.890 times" documentado para a senha SQL) — criar um App Registration
  novo pode exigir aprovação de time de identidade/AD fora do controle deste projeto, com prazo
  não previsível.

### Opção B — Identidade de serviço sintética, aceita só fora de produção *(descartada — ver Revisão)*

Um mecanismo **novo e paralelo** ao `TrustedIdentityMiddleware`, que nunca reaproveita a guarda
de loopback nem os headers `x-iis-*`. Um middleware dedicado
(`ServiceCredentialAuthenticationMiddleware` ou equivalente) reconhece um par de headers
próprios — por exemplo `X-Service-Credential` (segredo) + `X-Service-Name` (identificador fixo,
ex.: `cypress-e2e`) — e, se baterem contra um segredo configurado
(`Security:ServiceCredential:Secret`, via `dotnet user-secrets`/env var, nunca no
`appsettings.json`), popula `HttpContext.User` com uma identidade sintética própria: nome fixo
(`svc-cypress-e2e`), role dedicada (`servico-e2e`) — **não** `admin`, **não** reaproveita papéis
humanos.

**A trava que impediria isso de virar o próximo `ApiKeyGateFilter` (chave global fail-open):**

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
  cenário, migrar para a Opção A vira necessário, não opcional. **Este cenário se confirmou —
  ver Revisão abaixo.**

## Revisão da decisão (2026-09-03) — por que a Opção A agora

O dono pediu explicitamente a opção mais completa: client credentials real via Entra, porque
"o Cypress uma hora vai precisar fazer os testes end-to-end direto na API" — ou seja, o
cenário de gatilho que a recomendação original (Opção B) já havia identificado como sua própria
condição de expiração ("se o Cypress precisar rodar contra staging/produção real... migrar para
a Opção A") deixou de ser hipotético e virou direção declarada do produto.

**O que muda no cálculo, ponto a ponto:**

- O contra original de A ("overkill de escopo para 1 endpoint hoje") deixa de ser o critério
  certo — a pergunta não é mais "o que resolve `execute-lowcode` com menos código", é "o que não
  precisa ser redesenhado quando o Cypress crescer para bater na API de verdade, continuamente,
  possivelmente contra mais de um ambiente". A Opção B resolve o problema de hoje e cria dívida
  técnica garantida (a própria ADR original já previa a migração como certeza, não risco).
- O contra "não é identidade real/auditável" da Opção B se torna mais grave à medida que o
  Cypress passa a ser tráfego recorrente e não um gate pontual: sem identidade real no Entra,
  não há como distinguir "execução legítima do pipeline de CI" de "alguém reproduzindo o
  segredo do `X-Service-Credential` fora do CI" a não ser por convenção de nome de usuário — o
  App Registration do Entra dá revogação e trilha de auditoria organizacional de verdade.
- Implementar B agora e migrar para A depois (como o ADR original recomendava) significa pagar
  o custo de implementação **duas vezes** — o pedido do dono é para pagar uma vez só, já na
  opção que não precisa ser jogada fora.

**O que NÃO muda:** o raciocínio de rede (loopback vs. `127.0.0.1`) continua sendo o contra mais
sério de A e precisa de resposta honesta antes da implementação — tratado na seção dedicada
abaixo, não descartado.

## Mecanismo detalhado — Opção A (client credentials via Entra)

### Onde o `client_id`/`client_secret` ficam armazenados

Nunca em `appsettings.json` versionado — mesmo padrão já estabelecido para outros segredos do
projeto (`.claude/rules/security.md`, `docs/architecture/runbook-hardening-senha-sql-em-repouso.md`):

| Ambiente | Onde vive o segredo (lado Cypress/CI, quem *envia* o client_secret) |
|---|---|
| Dev local (dev rodando Cypress na workstation) | `dotnet user-secrets`? **Não** — o segredo aqui não é da API, é do **consumidor** (Cypress). Fica em `.env.local`/variável de ambiente do shell do Cypress, fora do repo `LayoutParserCypress`, no `.gitignore` local. |
| CI (GitHub Actions do `LayoutParserCypress`) | Secret do GitHub Actions (`CYPRESS_E2E_CLIENT_SECRET` ou equivalente) — nunca em log, nunca em `VITE_*`/bundle. |

Do lado da **API**, não há `client_secret` para armazenar — a API só **valida** tokens (verifica
assinatura contra o JWKS público do tenant Entra, confere `issuer`/`audience`). O único dado de
configuração novo na API é público por natureza (não é segredo):

```
Authentication:ServiceClient:Authority = https://login.microsoftonline.com/<tenant-id>/v2.0
Authentication:ServiceClient:Audience  = api://layoutparser-api   (ou o Application ID URI do App Registration da API)
```

Esses dois valores **podem** ir no `appsettings.json` (não são segredo — são metadados públicos
de configuração OIDC, o mesmo raciocínio que já vale para `Security:TrustedUserHeader`). O
`client_secret` do App Registration "de serviço" (o que o Cypress usa para logar) nunca passa
pela API — é trocado por um token diretamente com o Entra, do lado do Cypress.

### Como o middleware valida o token — novo scheme, não extensão do `TrustedIdentityMiddleware`

**Decisão: novo `AuthenticationScheme` paralelo (JWT Bearer), não extensão do
`TrustedIdentityMiddleware` existente.** Justificativa:

- `TrustedIdentityMiddleware` resolve um problema estruturalmente diferente: ele **confia** em
  headers **porque a rede garante a origem** (loopback = "só pode ser o BFF"). Um Bearer token
  JWT prova identidade **por criptografia** (assinatura verificável), independente de rede. São
  dois modelos de confiança diferentes — misturá-los no mesmo middleware acopla "quem confio
  porque a rede garante" com "quem confio porque a matemática garante", o que dificulta raciocinar
  sobre cada um isoladamente (e viola o princípio de que `TrustedIdentityMiddleware` deve
  permanecer simples o bastante pra auditar de cabeça).
- ASP.NET Core já suporta múltiplos `AuthenticationScheme`s nativamente (`AddAuthentication()`
  aceita registrar N schemes; `[Authorize]` sem `AuthenticationSchemes=` aceita qualquer scheme
  que autentique com sucesso). Não é gambiarra — é o padrão da própria plataforma para "múltiplas
  formas de provar quem você é".
- Registro: `builder.Services.AddAuthentication().AddJwtBearer("ServiceClient", options => {...})`
  como scheme adicional, ao lado do `TrustedHeaderAuthenticationHandler` já existente. O
  `[Authorize]` em `execute-lowcode` (e futuros endpoints) continua sem `AuthenticationSchemes=`
  explícito — aceita qualquer um dos dois schemes que autenticar.
- Populam a mesma claim de role (`ClaimTypes.Role`) que o resto do pipeline de autorização já
  entende — o token do Entra carrega uma **App Role** (ver abaixo) mapeada para o mesmo formato
  de role que `[Authorize(Roles = "...")]` já espera, para não duplicar lógica de autorização.

### Escopo/role mínimo — nunca admin

O App Registration "de serviço" ganha **uma única App Role** no Entra, ex. `Service.E2E`, exposta
como *application permission* (não *delegated* — não há usuário logado). O token emitido carrega
essa role na claim `roles`. Mapeamento no lado da API: `Service.E2E` → mesma role interna
`servico-e2e` que a Opção B já havia desenhado (reaproveitando o nome/escopo, só trocando o
mecanismo de prova de identidade). Igual à trava já prevista em B: qualquer endpoint que exija
`[Authorize(Roles = "admin")]` **não** aceita esse token — ampliar o escopo exige decisão nova
registrada, não herança automática.

### Rede: 127.0.0.1 vs. client credentials — avaliação honesta

Este é o trade-off que a Opção A original apontava como o contra mais sério, e continua sendo.
Três caminhos possíveis, em ordem de preferência:

1. **Preferido — túnel/proxy do CI para o loopback, sem abrir a rede da API.** Se o runner do
   Cypress em CI puder ser **co-localizado** com a API (mesma máquina/mesmo runner self-hosted
   que já hospeda o BFF, ou um túnel SSH/named pipe do job de CI para `127.0.0.1:5100` do host),
   a API continua escutando só em loopback — a trava de rede do `rollout-p2-autenticacao.md`
   **não precisa reabrir**. Isso é viável hoje se o runner de CI do `LayoutParserCypress` rodar
   na mesma máquina de dev (`BRNDDAPPBLD01`/workstation) onde a API já roda como serviço — a
   confirmar com `@lp-devops` se o runner atual do Cypress é local ou hospedado (GitHub-hosted
   runner genérico não teria acesso de rede a `127.0.0.1` da máquina de dev).
2. **Aceitável, com custo declarado — exceção pontual na trava de rede para o IP do runner.**
   Se o runner do Cypress não puder ser co-localizado (ex.: roda em runner GitHub-hosted, ou
   precisa testar contra staging real de fora), a API precisa aceitar conexões de um IP/rede
   específico do runner, além de `127.0.0.1`. Isso **é** uma reabertura parcial da superfície de
   rede — mas com uma diferença fundamental do estado anterior (headers confiáveis de qualquer
   origem): aqui a autenticação não depende mais da origem de rede ser confiável, e sim da posse
   do token JWT assinado. Abrir a porta para o IP do runner deixa de ser "confio em quem vier
   desse IP" (o problema que a guarda de loopback resolve) e passa a ser só alcançabilidade de
   rede — a autenticação real acontece depois, no JWT Bearer. Ainda assim, mais IP alcançável é
   mais superfície de ataque de rede (DDoS, port scan, exploits não relacionados a auth) —
   custo real, não zero.
3. **Descartado — abrir a API para `0.0.0.0` sem restrição.** Não necessário em nenhum cenário
   realista do Cypress; não deve ser considerado.

**Conclusão sobre rede:** o client credentials flow **não exige tecnicamente** abrir a rede além
de loopback **se** o runner puder ser co-localizado (caminho 1) — mas se não puder, o caminho 2
é um trade-off real que este ADR não pode fingir que não existe. Recomendação: `@lp-devops`
confirma a topologia do runner do Cypress **antes** da implementação; se for co-localizado,
implementar client credentials sem tocar na trava de rede; se não for, decidir explicitamente
(com o dono) se abrir a exceção de IP vale o ganho de identidade real — essa é uma decisão de
risco, não só de arquitetura, e deve ser registrada como adendo a este ADR quando resolvida.

## Recomendação (revisada)

**Opção A — client credentials via Entra.** Decisão do dono, fundamentada em direção de produto
declarada (Cypress vai bater na API real, de forma recorrente, não como exceção pontual). A
Opção B fica descartada como implementação — não como raciocínio: o desenho de role mínima,
trava de ambiente e nunca-no-browser da Opção B é reaproveitado dentro da Opção A (mesmo nome de
role `servico-e2e`/`Service.E2E`, mesmo princípio de nunca herdar `admin`).

## Honeypots / Canary Tokens — camada de detecção complementar

Pedido explícito do dono, como camada extra de segurança **complementar** aos controles de auth
reais acima — **isto é detecção, não prevenção.** Um honeypot nunca impede um ataque; ele existe
para que, se um controle de auth real falhar ou for contornado, exista um sinal de alarme barato
e de alta confiança (baixíssimo falso-positivo, porque nenhum tráfego legítimo deveria jamais
tocar nele).

### Desenho: 2 mecanismos-isca, independentes entre si

**1. Endpoint-isca (`honeypot route`).**

Uma rota que aparenta ser sensível/privilegiada mas não executa nenhuma lógica real — só
detecta e alarma. Candidatos de nome (escolher o que soa mais "convidativo" para alguém
enumerando rotas): `POST /api/admin/execute-raw`, `GET /api/internal/debug-config`, ou
`POST /api/TransformationExecution/execute-legacy` (imitando um endpoint "antigo" plausível
dado o padrão de nomenclatura já existente no controller real). Recomendo o último — encaixa no
padrão real de nomes do projeto, o que aumenta a chance de um atacante que já enumerou
`execute-lowcode`/`execute-candidates` achar plausível que `execute-legacy` também é real.

Comportamento do endpoint:
- Aceita **qualquer** request (sem exigir auth real — é isso que faz dele atrativo/plausível para
  quem está sondando).
- Não faz nada com o payload (não parseia, não persiste, não repassa a nenhum serviço real —
  risco zero de virar vetor de fato).
- Responde algo plausível (ex.: `202 Accepted` ou um JSON de erro genérico) para não denunciar
  que é uma armadilha via comportamento diferente do resto da API.
- **Todo** hit — sucesso ou não — dispara o alarme (ver mecanismo de alerta abaixo). Não há
  "uso legítimo" possível por definição: nenhum código do próprio sistema (React, MCP, Cypress
  real) jamais deveria chamar essa rota.

**2. Credencial-isca (`canary credential`).**

Um valor de `X-Service-Credential`/client_secret "aposentado" — gerado, documentado como válido
em algum artefato que um atacante plausivelmente encontraria (ex.: um `appsettings.json.example`
antigo, um commit histórico já limpo mas que pode ter sido clonado antes da limpeza, ou um
arquivo de config de ambiente de teste descontinuado) — mas que a API **continua reconhecendo**
apenas para fins de detecção, nunca para autorizar nada real.

Mecanismo: se a Opção A (client credentials Entra) for a única via de auth M2M viva, a credencial
canary não pode ser um App Registration real no Entra (isso daria acesso de verdade). Em vez
disso, é um valor de **API key legada, fixa, nunca rotacionada**, reconhecida por um middleware
dedicado (`CanaryCredentialDetectionMiddleware`) que:
- Compara o header/token recebido contra a lista de valores canary conhecidos (comparação de
  tempo constante, mesmo cuidado do `FixedTimeEquals` já usado em outros middlewares do projeto).
- Se bater, **nunca** autentica o request (`HttpContext.User` continua anônimo/rejeitado) — só
  dispara o alarme e retorna o mesmo 401 que qualquer credencial inválida receberia (não
  denuncia que foi detectado como canary, para não ensinar o atacante a evitar esse valor
  especificamente).
- Roda **antes** de qualquer middleware de auth real, para garantir que o alarme dispara mesmo
  que o valor canary por acaso colida com alguma validação futura.

### Mecanismo de alerta — nunca silencioso

Qualquer hit no endpoint-isca ou na credencial-isca gera:

1. **Log estruturado, nível `Critical` (acima de `Error`)**, com marcador distinto e
   correlacionável — ex. `_logger.LogCritical("CANARY_TRIGGERED {CanaryType} {SourceIp}
   {CorrelationId} {Payload}", ...)`. Nível `Critical` porque nenhum log operacional legítimo do
   projeto deveria usar esse nível hoje — torna o grep/alerta trivial de distinguir de ruído
   normal, e já é maior que qualquer coisa que o Serilog sink atual trata como "esperado".
2. **Correlação:** reaproveita o `CorrelationId` já injetado no `LogContext` (mesmo padrão do
   resto do projeto) para permitir cruzar com outros logs da mesma origem/IP se o atacante tentar
   mais de uma coisa na mesma sessão.
3. **Disparo automático — reaproveitar o step de e-mail já existente em `deploy.yml`.** O
   projeto já tem o padrão de e-mail via `dawidd6/action-send-mail@v3`, mas esse mecanismo hoje
   só dispara **dentro de um workflow de CI** (no contexto de um deploy) — não serve diretamente
   para um evento em runtime da API rodando em produção/dev. Duas opções, recomendo a primeira:
   - **(Recomendada) Sink dedicado no Serilog para nível `Critical` com gatilho de e-mail.**
     Serilog já suporta múltiplos sinks; adicionar um sink condicional (`MinimumLevel.Override`
     restrito a `Critical`, ou um `Serilog.Sinks.Email` — pacote gratuito, mesma filosofia de
     "sem infra nova" já usada no projeto) que dispara e-mail **só** quando um log `Critical`
     acontece. Reaproveita os mesmos secrets SMTP já documentados em `.claude/rules/security.md`
     (`SMTP_SERVER`/`SMTP_PORT`/`SMTP_USERNAME`/`SMTP_PASSWORD`/`ALERT_EMAIL_TO`) — mesma
     dependência de o dono escolher o provedor SMTP, hoje ainda pendente. Enquanto o provedor
     não for escolhido, o log `Critical` ainda existe (silencioso só no e-mail, não no log) —
     ainda é uma melhoria sobre "sem alarme nenhum".
   - **(Alternativa, mais trabalho) Webhook simples** de um serviço de log/monitoramento externo
     gratuito (ex. um endpoint HTTP que o middleware chama de forma fire-and-forget, nunca
     bloqueando o request) — mais superfície nova a manter, só vale se o dono já tiver um
     destino de webhook em mente (Discord/Telegram bot, por exemplo, ambos gratuitos). Não
     recomendo abrir essa frente sem um destino concreto já decidido.
4. **Nunca falha silenciosamente.** Igual ao resto do projeto (`.claude/rules/dotnet-standards.md`
   §Resiliência) — se o sink de e-mail falhar (SMTP fora do ar), o log `Critical` continua
   gravado localmente; a falha do alerta não pode mascarar a detecção.

### Limite explícito — detecção, não prevenção

Este mecanismo **não substitui** nenhum controle de auth real (Opção A, `TrustedIdentityMiddleware`,
`[Authorize]`). Ele não impede um ataque bem-sucedido, não reduz a superfície de rede, não
protege segredos reais. O único papel dele é: se algo/alguém que não deveria estar testando a
API acabar testando (enumeração de rotas, replay de segredo antigo vazado no histórico do git —
ver `.claude/rules/security.md`, pendência de limpeza de histórico), há uma chance real de
detectar isso antes/durante, em vez de só depois de um incidente confirmado por outros meios.

## Plano de implementação (revisado, para `@lp-backend-dev`)

### Parte 1 — Client credentials via Entra (Opção A)

1. **Pré-requisito de coordenação externa (não é `@lp-backend-dev`):** criar/confirmar 2 App
   Registrations no tenant Entra da NDD — (a) o App Registration "de serviço" que o Cypress usa
   para logar (`client_id`/`client_secret`, grant `client_credentials`); (b) confirmar que a API
   já tem (ou criar) seu próprio App Registration como *resource*/*audience*, com uma App Role
   `Service.E2E` exposta como *application permission*, concedida ao App Registration (a). Esta
   etapa depende de aprovação/execução do time de identidade/AD — sinalizar prazo não previsível
   ao dono antes de comprometer uma data de entrega.
2. **Confirmar topologia do runner do Cypress com `@lp-devops`** antes de tocar em rede — ver
   seção "Rede: 127.0.0.1 vs. client credentials" acima. Decisão de abrir ou não exceção de IP é
   do dono, registrar como adendo a este ADR quando resolvida.
3. **Pacote NuGet:** `Microsoft.Identity.Web` (ou `Microsoft.AspNetCore.Authentication.JwtBearer`
   puro, mais leve — avaliar qual encaixa melhor sem trazer dependências além do necessário para
   um App-only flow).
4. **Config nova em `appsettings.json`** (pública, não segredo):
   `Authentication:ServiceClient:Authority`, `Authentication:ServiceClient:Audience`.
5. **Registro em `Program.cs`:** `AddAuthentication().AddJwtBearer("ServiceClient", options => {
   options.Authority = ...; options.Audience = ...; })`, registrado como scheme adicional, sem
   remover o `TrustedHeaderAuthenticationHandler` existente. Mapear a claim `roles` do token
   (`Service.E2E`) para `ClaimTypes.Role = "servico-e2e"` via `options.TokenValidationParameters`
   ou um `OnTokenValidated` event — mesmo formato de role que o resto do pipeline já entende.
6. **Sem mudança nos `[Authorize]` existentes** — continuam sem `AuthenticationSchemes=`
   explícito, aceitando qualquer scheme que autentique com sucesso (headers do BFF **ou** Bearer
   Entra).
7. **CI/Cypress (fora deste repo, para `@lp-devops`/dono do `LayoutParserCypress`
   coordenarem):** Cypress obtém o token via `client_credentials` grant (chamada direta ao
   endpoint de token do Entra, antes de cada suíte ou com cache de token até expirar) e manda
   `Authorization: Bearer <token>` em todo `cy.request()` que hoje toma 401. `client_secret` fica
   como secret do GitHub Actions do repo `LayoutParserCypress`, nunca versionado.
8. **Teste automatizado:** confirmar que um token com role diferente de `Service.E2E` (ou sem
   role nenhuma) **não** autoriza `execute-lowcode` — equivalente ao teste de mutação da guarda
   de loopback já existente, adaptado para o novo scheme.

### Parte 2 — Honeypot/Canary

9. **Endpoint-isca:** novo controller/action mínimo (ex. `POST
   /api/TransformationExecution/execute-legacy`), sem `[Authorize]`, sem lógica real — só loga
   `Critical` e responde. Confirmar que não colide com nenhuma rota real existente.
10. **Credencial-isca:** gerar 1+ valores de API key "aposentada" plausíveis, documentar em local
    que um histórico de git limpo/exemplo de config antigo tornaria descobrível (coordenar com
    `@lp-devops` o que já está exposto no histórico pré-limpeza, para reaproveitar como isca real
    em vez de inventar uma nova). Middleware `CanaryCredentialDetectionMiddleware`, registrado
    bem no início do pipeline, antes de qualquer auth real.
11. **Log `Critical` estruturado** com marcador `CANARY_TRIGGERED`, `CorrelationId`, IP de
    origem, payload (truncado, nunca logar segredos de terceiros que porventura venham no
    payload).
12. **Sink de e-mail condicional a `Critical`** no Serilog — mesma dependência de secrets SMTP já
    pendente (`.claude/rules/security.md`); registrar como reaproveitamento explícito, não
    duplicar configuração.
13. **Teste automatizado:** confirmar que um hit no endpoint-isca ou na credencial-isca gera
    exatamente 1 log `Critical` com o marcador esperado, e que a resposta HTTP não denuncia a
    detecção (mesmo código/formato de um erro comum).

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| App Registration novo no Entra não é aprovado a tempo pelo time de identidade/AD | Escalar prazo ao dono cedo; não é um risco que `@lp-backend-dev` resolve sozinho — é dependência externa explícita desde o passo 1 do plano. |
| Exceção de rede para o IP do runner do Cypress aumenta superfície de ataque | Decisão explícita do dono após `@lp-devops` confirmar topologia (seção de rede); preferir túnel/co-localização sempre que viável. |
| `client_secret` do Cypress vaza (log, commit acidental, CI mal configurado) | Revogável no Entra sem tocar em código da API (diferencial sobre a Opção B); mesma disciplina de nunca versionar já aplicada a outros segredos do projeto. |
| Escopo da App Role `Service.E2E` cresce informalmente e vira um `admin` disfarçado | Mesma trava de princípio já registrada para a Opção B: qualquer ampliação de papel exige decisão nova registrada, não só um PR de código. |
| Endpoint-isca ou credencial-isca são descobertos e "aprendidos" por um atacante sofisticado que evita chamá-los | Aceito como limite conhecido de qualquer honeypot — valor está em pegar tentativas automatizadas/não sofisticadas (scanners, replay de segredo vazado), não um atacante direcionado que já conhece o mecanismo. Não é motivo para não implementar. |
| Sink de e-mail do canary depende do mesmo provedor SMTP ainda não escolhido pelo dono | Log `Critical` local continua funcionando mesmo sem e-mail configurado — degrada, não falha silenciosamente. |
| Confundir a credencial-isca com um mecanismo de auth real e alguém tentar "consertá-la" para funcionar de verdade | Documentar explicitamente no código (comentário PT-BR) que a credencial-isca **nunca** deve autenticar nada — é o oposto do propósito. |

## Nota sobre #219 — frente separada, não depende deste ADR

`generate-for-layout` não tem `[Authorize]` hoje; o erro reportado
(`"Tipo de layout não suportado: 2"`, HTTP 200 com `success:false`) é de validação de
`layoutType` do layout FIAT (`LAY_TXT_MQSERIES_ENVNFE_4.00_NFe`), não de autenticação. Nenhuma
parte do mecanismo M2M desta ADR afeta essa investigação — ela segue com `@lp-parser-llm`
(domínio de geração TCL/XSL), como a própria epic #221 já havia separado corretamente em
"Frente B". Confirmar via código/cadastro se `layoutType=2` deveria ser suportado pelo endpoint
(lacuna real) ou se é o cadastro do layout que está errado no banco.
