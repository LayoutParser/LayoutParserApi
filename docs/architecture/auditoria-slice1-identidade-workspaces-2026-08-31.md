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
