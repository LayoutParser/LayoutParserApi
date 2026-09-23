# Auditoria Slice 1 — identidade imutável e workspaces fiscais

> Autora: `@lp-architect` (Aria) · Data: 2026-08-31 · Escopo: só auditoria + design, sem código.
> Instrução do dono seguida à risca: **não recomeçar do zero**, reaproveitar o que existe.

## 0. Docs-fonte lidos (todos existem, nenhum inventado)

- `LayoutParserReact/docs/architecture/fiscal-document-platform.md`
- `LayoutParserReact/docs/contracts/fiscal-workspace-and-mapping-explanation-api.md`
- `LayoutParserReact/docs/architecture/adr/0004-sysmiddle-read-only-and-human-in-the-loop-authoring.md`
- Issues `#225` (feature, OPEN, milestone P0) e `#228` (gate, OPEN, milestone P0, bloqueada por #225)

`ai-assisted-fiscal-mapping-studio.md` e `fiscal-platform-roadmap.md` não foram necessários para o
Slice 1 (identidade/workspace) — o Draft/IA (Slice 2+) já está coberto pelo contrato lido.

## 1. O que já existe (reaproveitar, não recomeçar)

- **`TrustedIdentityMiddleware`** (`Services/Security/TrustedIdentityMiddleware.cs`): já resolve a
  identidade só sob guarda de loopback (`TrustIdentityFromLoopbackOnly`), degrada pra anônimo sem
  lançar, popula `ICurrentUser` (Scoped) + `HttpContext.User`. **Este é o ponto de extensão, não
  substituição** — a guarda de loopback e o padrão "nunca lança" continuam válidos para os headers
  novos.
- **`ICurrentUser`/`CurrentUser`**: contrato limpo (`Name`, `Roles`, `IsAuthenticated`, `IsInRole`).
  Falta `UserId` (GUID interno) e não tem noção de workspace — extensão aditiva, não quebra.
- **Precedente de particionamento por usuário**: `AiCandidateStore` (issue #92, corrigido
  `c12dfa2`/`d917129`) já parte estado em disco por `ICurrentUser.Name`. É a prova de conceito real
  de "isolar por dono" nesta API — mas particiona por **nome mutável**, exatamente o anti-padrão que
  #225 pede pra eliminar. Vira o primeiro consumidor a migrar de `Name` → `WorkspaceId`/`UserId`
  quando o modelo novo existir (ver §4).
- **Contrato cross-repo já fechado**: `fiscal-workspace-and-mapping-explanation-api.md` já define
  headers (`x-layoutparser-identity-provider/subject/tenant`), endpoints (`GET /api/workspaces/me`,
  `GET /api/workspaces/{id}`, `POST .../projects`) e os 10 critérios de aceite. **Não há decisão de
  design em aberto do lado do contrato** — o trabalho da API é implementar contra ele, não reabatê-lo.
- **Issues #225/#228**: nenhum comentário de progresso além da descrição original (#225 tem 1
  comentário — a issue-mãe; #228 tem 0). **Nenhum trabalho de código começou.** Grep por
  `Workspace|ExternalIdentity|FiscalProject|WorkspaceMembership` no C# não encontra nenhum tipo de
  domínio novo — só matches irrelevantes (XML docs do Roslyn em `tools/LowCodeRunner`). Confirmado:
  Slice 1 está 0% implementado, mas 100% especificado no contrato.

## 2. O que falta construir

Nada do modelo de domínio novo existe. Do zero:

1. **Domínio**: `ExternalIdentity` (chave `provider+tenant/issuer+subject`), `UserId` (GUID interno),
   `FiscalWorkspace`, `WorkspaceMembership` (role: Owner/FiscalAdmin/Mapper/Reviewer/Operator/Viewer).
2. **Persistência**: tabelas SQL novas (`Users`, `ExternalIdentities`, `Workspaces`,
   `WorkspaceMemberships`) — SQL continua fonte da verdade, nada disso é candidato a Redis-only.
3. **Middleware novo/estendido**: ler `x-layoutparser-identity-provider/subject/tenant` sob a mesma
   guarda de loopback, resolver/criar `ExternalIdentity`→`UserId` (upsert idempotente), criar
   workspace pessoal na primeira resolução se ainda não existir membership.
4. **`ICurrentUser` estendido**: `UserId` (Guid?), `ActiveWorkspaceId`, memberships resolvidas.
5. **Endpoints**: `GET /api/workspaces/me`, `GET /api/workspaces/{workspaceId}`,
   `POST /api/workspaces/{workspaceId}/projects` (Slice 1 só entrega os dois primeiros; o de
   projects é Slice 1.5/2, mas o modelo de domínio precisa já prever `FiscalProject` como filho).
6. **Middleware de autorização por workspace**: todo endpoint que aceita `{workspaceId}` precisa
   validar membership antes de tocar dado — hoje **nenhum endpoint faz isso** porque não existe
   conceito de workspace.

## 3. Design proposto — encaixe com o que existe

```
x-layoutparser-identity-{provider,subject,tenant}  (headers novos, mesma guarda de loopback)
        ↓ TrustedIdentityMiddleware (estendido, não substituído)
IExternalIdentityResolver.ResolveOrCreateAsync(provider, subject, tenant)
        ↓ upsert em ExternalIdentities → Users (SQL, idempotente por UNIQUE constraint)
CurrentUser.Set(name, roles, userId)          ← extensão aditiva de CurrentUser existente
        ↓
IWorkspaceMembershipProvider.GetOrCreatePersonalWorkspaceAsync(userId)  ← lazy, idempotente
        ↓
ICurrentUser.ActiveWorkspaceId / Memberships   ← novo, consumido por controllers/filtros
```

**Convivência em transição** (pedido explícito do dono — "substituir gradualmente"):
- `x-iis-user`/`x-iis-roles` continuam sendo lidos e populando `Name`/`Roles` — **não removidos**
  nesta fase. `Name` some como chave de particionamento, mas continua existindo como atributo de
  exibição/auditoria (mesmo papel que o contrato já define: "nome/e-mail nunca é chave").
- Se os headers novos (`x-layoutparser-identity-*`) estiverem ausentes (BFF ainda não manda), o
  middleware degrada: `UserId`/`ActiveWorkspaceId` ficam `null`, sem lançar — mesmo padrão
  "fail-open pra anônimo, fail-closed pra dado" já usado hoje.
- `AiCandidateStore` continua particionado por `Name` até ganhar uma migração dedicada (ver §5) —
  **não migrar dentro do Slice 1**, é trabalho separado e não bloqueia `#225`/`#228`.

## 4. Plano de migração gradual

1. **Slice 1a** (este): domínio + persistência + middleware + `GET /api/workspaces/me` +
   `GET /api/workspaces/{id}`. `AiCandidateStore` e demais consumidores de `ICurrentUser.Name`
   **não mudam ainda**.
2. **Slice 1b**: `#228` — bateria de testes de isolamento cross-workspace (matriz positiva/negativa,
   forjar headers fora da guarda de loopback, mudança de nome preserva `UserId`). Gate de release,
   não pode ser pulado antes de qualquer endpoint novo expor dado por workspace.
3. **Slice 2** (fora deste pedido): migrar `AiCandidateStore` de partição por `Name` para partição
   por `WorkspaceId`/`UserId` — documentar o mapeamento de dados legados (pasta hoje é
   `MLData/AiTransformationCandidates/{Name}/`; a migração precisa decidir se renomeia pastas
   existentes pra `{UserId}` ou se trata como perda aceitável de histórico, dado que é cache/job
   avulso, não fonte da verdade).
4. **Slice 3**: `POST /api/workspaces/{id}/projects` e o restante do catálogo de mappings do
   contrato — cadeia mais longa (packages, drafts, compile, test-runs).

## 5. Riscos de segurança específicos deste slice

- **Isolamento cross-workspace**: todo endpoint com `{workspaceId}` na rota precisa validar
  membership *antes* de tocar o repositório — proponho um `ServiceFilter` novo
  (`WorkspaceMembershipFilter`, mesmo padrão do `AuditActionFilter` já existente) em vez de repetir
  o check em cada controller. "Não existe" e "existe mas não é seu" devem retornar o mesmo 404
  (contrato já exige isso — §2 do contrato).
- **Fail-closed por padrão**: se `ActiveWorkspaceId` for `null` (headers novos ausentes ou falha na
  resolução), qualquer endpoint que dependa de workspace **nega**, não degrada pra "sem filtro" —
  diferente do padrão atual de `ICurrentUser` (que degrada pra anônimo mas ainda deixa endpoints
  sem `[Authorize]` responderem). Aqui a degradação correta é **recusar**, não abrir.
- **Idempotência de criação de workspace pessoal sob concorrência**: duas requisições simultâneas do
  mesmo `UserId` novo não podem criar dois workspaces pessoais. Resolver com `UNIQUE` constraint
  SQL em `(UserId, Kind='personal')` + `INSERT ... WHERE NOT EXISTS` (ou `MERGE`) dentro de uma
  transação — não confiar em lock em memória (a API pode escalar horizontalmente no futuro).
- **`subject` nunca vaza**: não deve aparecer em log, resposta HTTP nem em mensagem de erro — só o
  `UserId` interno é observável fora da resolução. Reforça a regra já existente em
  `.claude/rules/security.md` de nunca logar conteúdo sensível.
- **Headers legados como vetor de downgrade**: se o BFF ainda manda `x-iis-user` mas não os headers
  novos, o middleware não deve inferir workspace a partir de `Name` — isso recriaria a propriedade
  por nome que #225 quer eliminar. Ausência dos headers novos = sem workspace, ponto.

## 6. Contratos iniciais propostos (conforme o contrato cross-repo, sem reabrir decisão)

```
GET /api/workspaces/me
  → 200 { activeWorkspaceId, workspaces: [{ workspaceId, name, kind, role, createdAt }] }
  → cria workspace pessoal idempotente na primeira chamada de um UserId sem membership
  → 401/identidade anônima: hoje não há [Authorize] — decisão de produto em aberto
    (mesma pendência já registrada em rollout-p2-autenticacao.md); recomendo que este endpoint
    específico EXIJA identidade resolvida (UserId != null) mesmo sem [Authorize] global, retornando
    401 explícito — é o primeiro endpoint em que "anônimo responde algo" deixa de fazer sentido.

GET /api/workspaces/{workspaceId}
  → 200 só se houver membership; senão 404 (nunca 403 — não revelar existência)
```

## 7. Primeiro passo executável para `@lp-backend-dev`

1. Criar as 4 entidades de domínio (`ExternalIdentity`, `User`, `FiscalWorkspace`,
   `WorkspaceMembership`) + migração SQL correspondente, seguindo o padrão de acesso a dado já usado
   por `MapperDatabaseService`/`CachedMapperService` (mesmo grupo de DI "Database" do `Program.cs`).
2. Estender `TrustedIdentityMiddleware` para ler os 3 headers novos sob a mesma guarda de loopback,
   chamando um `IExternalIdentityResolver` novo (Scoped) que faz upsert idempotente e popula
   `CurrentUser.UserId`/`ActiveWorkspaceId` (extensão aditiva de `ICurrentUser`/`CurrentUser`, sem
   quebrar `Name`/`Roles` existentes).
3. Implementar `GET /api/workspaces/me` (cria workspace pessoal on-demand) e
   `GET /api/workspaces/{workspaceId}` (404 fail-closed) num `WorkspacesController` novo.
4. Não tocar em `AiCandidateStore` nem em `TransformationExecutionController.CurrentUserId` neste
   slice — é migração separada (§4, Slice 2).
5. Handoff para `@lp-qa`: implementar os testes de `#228` (matriz cross-workspace) antes de liberar
   qualquer endpoint que devolva dado por `workspaceId` além do `me`.

Nenhum código foi escrito nesta sessão — só este documento, commitado localmente sem push.

## Implementação — 2026-08-31 (@lp-backend-dev)

Slice 1a e 1b implementados em cima do plano acima, sem desvio de arquitetura. `dotnet build` e
`dotnet test` (437/437) verdes.

### Arquivos criados

- `Models/Entities/Identity/{FiscalUser,ExternalIdentity,FiscalWorkspace,WorkspaceMembership}.cs` —
  domínio, incluindo `WorkspaceKind`/`WorkspaceRole` como constantes (não enum, para não travar
  novos papéis a uma recompilação).
- `Services/Interfaces/IIdentityWorkspaceStore.cs` (+ `WorkspaceSummary` record) — camada de
  persistência crua, existe como interface (não só a implementação SQL) especificamente para os
  testes de isolamento não dependerem de SQL Server real.
- `Services/Interfaces/IIdentityWorkspaceService.cs` (+ `WorkspaceMeResult` record) — orquestração
  com política fail-closed.
- `Services/Database/SqlIdentityWorkspaceStore.cs` — ADO.NET cru, mesmo padrão de
  `MapperDatabaseService`, mesmo banco `ConnectUS_Macgyver` (não há banco dedicado). DDL idempotente
  (`IF OBJECT_ID(...) IS NULL`) executado uma vez por processo. Tabelas: `tbUser`,
  `tbExternalIdentity` (UNIQUE `Provider+TenantOrIssuer+Subject`), `tbFiscalWorkspace` (índice
  filtrado único por `OwnerUserId` onde `Kind='personal'`), `tbWorkspaceMembership` (UNIQUE
  `WorkspaceId+UserId`). Corrida de INSERT tratada por captura de erro 2601/2627 + releitura —
  não por `SELECT` prévio como garantia (só como fast-path).
- `Services/Identity/IdentityWorkspaceService.cs` — trava em processo (`SemaphoreSlim` por chave de
  identidade/usuário) como primeira camada de defesa contra duplicidade, por cima do UNIQUE
  constraint do SQL (garantia definitiva, multi-instância).
- `Controllers/WorkspacesController.cs` — os dois endpoints do Slice 1.
- Testes: `tests/.../Controllers/WorkspacesControllerTests.cs` (isolamento cross-workspace, 404
  uniforme, 401 no `/me` sem identidade, degradação 503 em falha de SQL) e
  `tests/.../Services/Identity/IdentityWorkspaceServiceTests.cs` (idempotência sob concorrência em
  processo, fail-closed, subject nunca logado).

### Arquivos estendidos (não substituídos)

- `Services/Interfaces/ICurrentUser.cs` / `Services/Security/CurrentUser.cs` — `UserId` (Guid?)
  aditivo, `SetUserId` internal.
- `Services/Security/TrustedIdentityOptions.cs` — 3 headers novos com os defaults do contrato.
- `Services/Security/TrustedIdentityMiddleware.cs` — `InvokeAsync` ganhou o parâmetro
  `IIdentityWorkspaceService` (resolvido via DI Scoped, mesmo mecanismo do `ICurrentUser`); os
  headers novos são lidos sob a MESMA guarda de loopback, depois (não em vez) dos legados. Ausência
  dos headers novos = `UserId` fica `null`, sem inferir de `Name` — confirmado por teste dedicado.
- `Program.cs` — `IIdentityWorkspaceStore`/`IIdentityWorkspaceService` registrados no grupo Database.
- Testes existentes que tinham fakes de `ICurrentUser`/chamavam `TrustedIdentityMiddleware.InvokeAsync`
  diretamente (`AuditActionFilterTests`, `RoleAuthorizationTests`,
  `TransformationExecutionController*Tests`) foram ajustados para a assinatura nova — nenhum teve
  asserção alterada, só a superfície de compilação.

### Desvios do plano original

1. **`WorkspaceRole`/`WorkspaceKind` como `static class` de constantes `string`, não `enum`.**
   A auditoria não especificou o tipo; strings casam diretamente com as colunas `NVARCHAR` do SQL
   sem conversão, e o contrato cross-repo já usa strings minúsculas (`"owner"`, `"personal"`) no
   JSON — evita um `enum` com `[JsonConverter]`/mapeamento redundante.
2. **Trava em processo além do UNIQUE do SQL**, não mencionada explicitamente no plano (que citava
   só o UNIQUE constraint). Adicionada porque o teste de concorrência exigido pelo pedido do dono
   (`#17`) precisa de uma garantia testável sem SQL Server real — o lock em processo é essa
   garantia, testada de fato; o UNIQUE constraint é a rede de segurança multi-instância, coberta só
   por revisão de DDL nesta sessão (sem ambiente SQL Server disponível para um teste de integração
   real).
3. **`POST /api/workspaces/{workspaceId}/projects` não foi criado** — a auditoria já previa isso
   como Slice 1.5/2 (§4, item 4), só confirmando que o escopo foi respeitado.

### Limitação conhecida (documentar, não esconder)

A garantia de idempotência sob concorrência **multi-instância/multi-processo** depende do UNIQUE
constraint do SQL Server (`UQ_tbExternalIdentity`, índice filtrado `UX_tbFiscalWorkspace_
PersonalOwner`, `UQ_tbWorkspaceMembership`) e do tratamento de erro 2601/2627 em
`SqlIdentityWorkspaceStore` — revisados por leitura de código, mas **não exercitados por um teste
de integração real** neste ambiente (sem acesso a SQL Server). Os testes automatizados cobrem a
idempotência **em processo** (via o lock do `IdentityWorkspaceService`, com um `RaceyFakeStore` que
simula a janela de corrida). Recomendação para `@lp-qa`: validar a corrida real contra o SQL de
desenvolvimento antes de liberar tráfego de produção com múltiplas instâncias da API.
