# Design — Slice 3: `MappingDraft` human-in-the-loop (issue #230)

> Autora: `@lp-architect` (Aria) · 2026-08-31 · Só design, sem código. Segue a sequência vertical
> dos Slices 1 (PR #234) e 2 (PR #236). Releia seção 8 do prompt original antes de implementar.

## 1. Investigação prévia (achados)

- **IA de sugestão ≠ `RepairOrchestrator`.** O `RepairOrchestrator`/`RepairOrchestratorXslSynthesizerService`
  parte de um `MapperVo` já existente e sintetiza/repara **XSLT executável** via loop gerar→validar
  XSD→diff→corrigir. Slice 3 é upstream disso: o LLM lê planilha/XSD/amostra (artefatos do
  `FiscalMappingPackage`, Slice 2) e propõe **regras de mapeamento estruturadas**, não código. É um
  prompt/fluxo novo, mas reaproveita a infra de baixo nível: mesmo `HttpClient`/`OllamaOptions`,
  mesmo padrão de chamada ao Ollama local (nunca nuvem, `security.md`), e o `CanonicalDiffer` pode
  servir depois (Slice 5, geração) para comparar saída contra gabarito — não aqui.
- **Padrão de job assíncrono já existe e deve ser reaproveitado**: `AiTransformationCandidateService`
  usa `IServiceScopeFactory` para abrir um `Task.Run` fire-and-forget que sobrevive ao fim do scope
  HTTP, porque os serviços que consome (`XsdValidationService`, `IXslSynthesizerService`) são
  `Scoped`. Mesmo mecanismo aqui: `POST .../suggestions` inicia o job e retorna `202 Accepted` com
  um `jobId`; o job roda em scope próprio, grava progresso/resultado, nunca bloqueia a resposta.
- **Concorrência otimista via ETag é greenfield.** Nenhum controller do projeto usa `ETag`/`If-Match`
  hoje (`grep` sem resultado). Desenho novo, baseado em `RowVersion` (`ROWVERSION`/`TIMESTAMP` do SQL
  Server), que é exatamente o mecanismo que o SQL já oferece para isso — não reinventar com hash
  manual.
- **Fronteira Sysmiddle**: seção 4 do prompt original — Sysmiddle só executa/explica, nunca autoria.
  Hoje não existe checagem centralizada de `engine=sysmiddle` em nenhum lugar do código (grep sem
  match) — é dívida que o Slice 3 introduz e que os Slices 4/5 também vão precisar. Resolver uma vez,
  não repetir.
- **Dado fiscal sensível e Ollama local**: já resolvido pela decisão de decommission de
  Gemini/OpenAI (`.claude/agent-memory/lp-architect/gemini-openai-decommission-decision.md`) —
  Ollama local assume 100% do papel de LLM neste projeto. Confirmado: nenhuma chamada de rede externa
  precisa ser desenhada aqui, a política "não sai pra nuvem sem autorização explícita" já está
  satisfeita pela arquitetura vigente.

## 2. Modelo de domínio

```
MappingDraft (1) ──< MappingDraftRule (N)
     │                      │
PackageId (FK,          Status: proposed|accepted|edited|rejected|needs_input|
Slice 2)                     validated|superseded
RevisionId (FK exato,    SourceRefs[] / TargetRefs[] (XPath/campo)
imutável)                Operation (copy|concat|lookup|conditional|constant|...)
WorkspaceId (FK,          Conditions (JSON estruturado)
Slice 1, isolamento)      Transformations (JSON estruturado)
CreatedByJobId            Cardinality (1:1|1:N|N:1)
RowVersion (ETag)          Evidence[] { Kind: SpreadsheetCell|XsdPath|SampleLine, Ref }
                            Confidence (0..1)
                            OpenQuestions[] (texto — presente quando Status=needs_input)
                            DecidedBy { UserId, At, Justification } (nulo até decisão)
                            RowVersion (ETag da regra, granularidade fina)
```

- **Draft é sempre filho de uma `FiscalMappingPackageRevision` exata** (Slice 2) — nunca "a revisão
  mais recente" implícita, para não mudar o material-fonte debaixo de um draft em revisão.
- **`needs_input` é obrigatório, não best-effort**: o job de sugestão nunca inventa mapping sem
  evidência suficiente — se a confiança/evidência não bate um limiar configurável, a regra nasce
  `needs_input` com `OpenQuestions` preenchido, nunca `proposed` com confiança fabricada.
- **Toda transição de estado grava decisão**: ator (`ICurrentUser.UserId`, não `Name` — mesmo
  princípio do Slice 1), instante, `RevisionId` do pacote no momento da decisão, justificativa
  (obrigatória para `rejected`/`edited`, opcional para `accepted`). Append-only em
  `tbMappingDraftRuleDecision`, nunca overwrite — auditoria completa.
- **`superseded`**: quando uma nova rodada de sugestão (nova chamada a `.../suggestions`) gera uma
  regra que cobre o mesmo `TargetRefs` de uma regra já decidida, a antiga vira `superseded`, nunca é
  apagada.

## 3. Concorrência otimista (ETag)

- `MappingDraftRule.RowVersion` = `ROWVERSION` SQL Server (8 bytes, auto-incrementado pelo motor a
  cada `UPDATE`). Mapeado para header HTTP `ETag: "<base64(RowVersion)>"` no `GET`/resposta de
  criação.
- `PATCH .../rules/{ruleId}` **exige** `If-Match: "<etag>"`. Sem o header → `428 Precondition
  Required`. Com header divergente do `RowVersion` atual → `412 Precondition Failed` (outro
  usuário/job já alterou a regra), corpo com o estado atual para o cliente decidir merge.
- Implementação: `UPDATE tbMappingDraftRule SET ... WHERE Id=@id AND RowVersion=@expectedRowVersion`
  — `RowCount=0` distingue "não existe"(404, fail-closed igual Slice 1/2) de "conflito"(412) por uma
  consulta de existência separada só nesse caso.
- Job de sugestão (`.../suggestions`) que gera regras novas não usa `If-Match` — é `INSERT`, não
  `UPDATE`; conflito só existe em edição humana concorrente ou humano-vs-nova-rodada-de-IA (resolvido
  por `superseded`, não por ETag).

## 4. Fronteira Sysmiddle — recusa centralizada

- `MappingEngineGuardFilter` (`IAsyncActionFilter`, mesmo padrão de `ServiceFilter` que
  `AuditActionFilter`/`WorkspaceMembershipFilter`): lê `engine` do body/query de qualquer request sob
  os controllers de autoria (Slice 3/4/5) e retorna `422` com mensagem explícita
  ("sysmiddle é somente leitura/explicação — autoria via tcl/xslt") se `engine=="sysmiddle"`.
- Aplicado via atributo `[ServiceFilter(typeof(MappingEngineGuardFilter))]` no controller inteiro
  (não por endpoint), para que Slices 4/5 herdem a checagem automaticamente ao reaproveitar o mesmo
  atributo — não recriar o `if` em cada action.
- Motor default quando ausente: **rejeitar ambiguidade**, não assumir `tcl`/`xslt` silenciosamente —
  exigir `engine` explícito no payload de criação do draft.

## 5. Endpoints propostos

```
POST /api/workspaces/{workspaceId}/mapping-packages/{packageId}/drafts
  body: { revisionId, engine: "tcl"|"xslt" }
  → 201 { draftId, packageId, revisionId, engine, rowVersion }
  → 422 se engine=sysmiddle · 404 fail-closed se sem membership/pacote/revisão

POST /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/suggestions
  body: { } (usa artefatos da revisão já vinculada)
  → 202 { jobId, status: "queued" } — fire-and-forget via IServiceScopeFactory,
    idempotente por (draftId, hash dos artefatos da revisão): reenviar não duplica job em execução
  → GET /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/suggestions/{jobId}
    → 200 { status: queued|running|completed|failed|canceled, rulesCreated, error? }
  → DELETE .../suggestions/{jobId} → cancelamento cooperativo (CancellationTokenSource por job)

PATCH /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/rules/{ruleId}
  header: If-Match obrigatório
  body: { status: accepted|edited|rejected, justification?, edits? (sourceRefs/targetRefs/... se edited),
          answer? (se respondendo a needs_input) }
  → 200 { rule atualizada, novo rowVersion/ETag }
  → 428 sem If-Match · 412 conflito · 404 fail-closed
```

Todos sob `WorkspaceMembershipFilter` (Slice 1) + `MappingEngineGuardFilter` (novo, §4).

## 6. Plano de execução — `@lp-backend-dev`

1. Entidades `MappingDraft`, `MappingDraftRule`, `MappingDraftRuleDecision` em
   `Models/Entities/Fiscal/`, status como `static class` de constantes (padrão Slice 1/2).
2. `IMappingDraftStore` (SQL cru, ADO.NET, mesmo padrão `SqlFiscalPackageStore`) com `ROWVERSION` nas
   tabelas de regra.
3. `IMappingSuggestionService` (novo prompt Ollama: entrada = artefatos da revisão via
   `IFiscalPackageService`, saída = lista estruturada de `MappingDraftRule` candidatas) — job
   fire-and-forget via `IServiceScopeFactory`, mesmo padrão de `AiTransformationCandidateService`.
   Não reutiliza `RepairOrchestrator` diretamente; reutiliza o `HttpClient`/`OllamaOptions` de baixo
   nível.
4. `MappingEngineGuardFilter` novo em `Services/Security/` (ou pasta de filtros existente),
   `[ServiceFilter]` no nível do controller.
5. `MappingDraftsController` com os 3 endpoints (+ GET/DELETE de job) sob os dois filtros.
6. Handoff `@lp-qa`: teste de `needs_input` obrigatório sob baixa evidência, teste de conflito ETag
   (`412`), teste de recusa `engine=sysmiddle` em todas as rotas, teste de cancelamento de job.

## 7. Decisões-chave (resumo)

- Sugestão via IA é fluxo novo (prompt de análise, não geração de XSLT) — reaproveita infra Ollama
  de baixo nível, não o `RepairOrchestrator`.
- Job assíncrono reaproveita 100% o padrão `IServiceScopeFactory` fire-and-forget já validado.
- ETag é greenfield baseado em `ROWVERSION` SQL nativo — não hash manual.
- Recusa de `engine=sysmiddle` centralizada em um `ServiceFilter` reaproveitável pelos Slices 4/5.
- Política de dado sensível já satisfeita pela decisão de decommission Gemini/OpenAI — nada novo a
  desenhar aqui.

## Primeiro passo executável

`@lp-backend-dev`: criar `Models/Entities/Fiscal/{MappingDraft,MappingDraftRule,
MappingDraftRuleDecision}.cs` + DDL idempotente em `SqlMappingDraftStore` com coluna `ROWVERSION`,
seguindo exatamente o padrão de `SqlFiscalPackageStore` (Slice 2). Nenhum código foi escrito nesta
sessão — commit local, sem push.

## Implementação — 2026-08-31 (`@lp-backend-dev`)

Build verde (`dotnet build`, 0 erros) e suíte completa verde (`dotnet test`, 481/481, incluindo os
12 testes novos deste slice).

**Arquivos criados:**
- `Models/Entities/Fiscal/MappingDraft.cs` — `MappingDraft`, `MappingDraftRule`,
  `MappingDraftRuleDecision`, `MappingDraftRuleStatus` (static class de constantes, padrão Slice 1/2).
- `Services/Interfaces/IMappingDraftStore.cs` — DTOs (`MappingDraftDetail`, `MappingDraftRuleDetail`
  com `ETag` já em base64, `UpdateRuleOutcome`/`UpdateRuleResult`) + interface.
- `Services/Database/SqlMappingDraftStore.cs` — ADO.NET cru, DDL idempotente (`tbMappingDraft`,
  `tbMappingDraftRule` com coluna `RowVersion ROWVERSION NOT NULL`, `tbMappingDraftRuleDecision`).
  Só LÊ as tabelas do Slice 2 (`tbFiscalMappingPackageRevision`/`tbPackageArtifact`) — nunca escreve
  nelas.
- `Services/Interfaces/IMappingSuggestionService.cs` + `Services/Fiscal/MappingSuggestionService.cs`
  — job fire-and-forget via `IServiceScopeFactory` (mesmo padrão de `AiTransformationCandidateService`),
  estado em memória (`ConcurrentDictionary`, mesmo espírito de `AiCandidateStore`), idempotente por
  hash dos `ArtifactId` da revisão, cancelamento cooperativo via `CancellationTokenSource` por job.
  Prompt novo (não reutiliza `RepairOrchestrator`) pedindo JSON estruturado ao Ollama
  (`format: "json"`); parseia a resposta e força `needs_input` sempre que evidência ou confiança
  vierem insuficientes — **independente do que o modelo alegou** (regra aplicada no parser, não
  delegada ao LLM).
- `Services/Filters/MappingEngineGuardFilter.cs` — `IAsyncActionFilter`, lê `engine` de query ou
  body JSON (bufferizado via `EnableBuffering`, sem consumir o stream original), recusa
  `engine=sysmiddle` com 422. `[ServiceFilter]` no nível do `MappingDraftsController` inteiro.
- `Controllers/MappingDraftsController.cs` — 5 rotas: `POST drafts`, `GET drafts/{id}`,
  `POST suggestions`, `GET suggestions/{jobId}`, `DELETE suggestions/{jobId}` (cancelamento) +
  `PATCH rules/{ruleId}`.
- Testes: `tests/.../Filters/MappingEngineGuardFilterTests.cs` (5 casos — sysmiddle recusado,
  case-insensitive, engines válidos passam, ausência de engine não é bloqueada pelo filtro) e
  `tests/.../Controllers/MappingDraftsControllerTests.cs` (7 casos — 428 sem If-Match, 412 com
  If-Match divergente, sucesso com If-Match correto + ETag muda depois, `rejected` sem justificativa
  é recusado, isolamento cross-workspace no GET, `engine` ausente no POST de criação é recusado,
  `POST suggestions` retorna 202 sem aguardar um job "pesado" — via `TaskCompletionSource` que só é
  liberado DEPOIS da asserção).

**Decisões tomadas durante a implementação (não estavam 100% fechadas no design):**
- `PATCH` com `answer` (resposta a `needs_input`) faz a regra voltar a `proposed` para nova avaliação
  humana — o design não especificava o status resultante.
- `RevisionBelongsToPackageAsync`/`GetArtifactFilesForRevisionAsync` foram colocados em
  `IMappingDraftStore` (não em `IFiscalPackageStore`) para não tocar código do Slice 2 além de
  reaproveitar suas tabelas via leitura direta — decisão de escopo, não de arquitetura.
- Conteúdo de planilha (`Kind=spec`, XLSX binário) não é extraído para o prompt neste slice — só um
  placeholder com o tamanho em bytes. Extração de texto de XLSX fica para uma iteração futura
  (fora do escopo da issue #230; XSD e sample, que são texto puro, já entram no prompt inteiros).
- `IMappingDraftStore.UpdateRuleStatusAsync` distingue `NotFound`/`Conflict` com uma segunda consulta
  só no caminho de falha (`RowCount=0`), exatamente como o design previa em §3.

**Handoff:**
- `@lp-doc`: nenhum endpoint pré-existente mudou de contrato — só rotas novas. Atualizar
  Swagger/README com os 5 endpoints novos de `MappingDraftsController` quando conveniente.
- `@lp-qa`: cobertura de controller/filtro feita nesta sessão; ainda faltam testes de
  `SqlMappingDraftStore` contra SQL real (idempotência do job, `superseded`, DDL) — não cobertos
  aqui por não haver SQL Server disponível neste ambiente de execução.
