# ADR — Histórico de análises fiscais com `AnalysisId` durável (issue #366)

**Status:** Proposto · **Data:** 2026-09-19 · **Autora:** Aria (`@lp-architect`)
**Origem:** bloqueio P0 do `LayoutParserReact#197` (histórico seguro por workspace).
**Escopo confirmado pelo dono (2026-09-18):** uma "análise fiscal" = **os arquivos que o usuário anexou junto com o layout**. Recuperar o que foi submetido; **não** é snapshot de parse + candidatos + transformação.

## 1. Estado atual (lido do código)

| Fato | Onde | Implicação |
|------|------|-----------|
| Upload de análise chega em `POST /api/parse/upload` (`layoutFile` + `txtFile`, multipart) e `POST /api/parse/auto` (só `documentFile`; layout vem do catálogo por GUID) | `Controllers/ParseController.cs` | Nenhum dos dois tem `{workspaceId}` na rota nem `AnalysisId`; `/auto` já bufferiza `documentBytes` em memória. |
| O único "salvamento" hoje é `SaveFileForLearningAsync`: grava o TXT em `TransformationPipeline:ExamplesPath\{layoutName}\{timestamp}_{guid}.ext` | `ParseController` | Sem dono, sem workspace, só o documento (não o layout); é **corpus de aprendizado**, não histórico do usuário. **Não reaproveitar**: misturaria dado de cliente com dataset de IA. |
| `tbLpAiUserSession`/`tbLpAiUserSessionHistoryEntry` (#102) guardam `Ticket` + `Status` por `UserId` | `SqlAiUserSessionStore` | É histórico de **jobs de IA**, sem arquivos e sem workspace. Não serve como modelo de dados; **serve como padrão** de retenção (`HistoryRetentionDays` + `BackgroundService` de purga, 180 d) e de `EnsureSchemaAsync`. |
| Pacote fiscal: `tbFiscalMappingPackage(Revision)` + `tbPackageArtifact` (Sha256, SizeBytes, OriginalFileName, MimeSniffed, UploadedByUserId, Classification, RetentionPolicy, StoragePath, Provenance); bytes em disco `ML:FiscalMappingPackagesPath\{workspaceId}\...` | `SqlFiscalPackageStore`, `FiscalPackageService` | Existe o **padrão** (metadado em SQL + blob em disco por workspace + hash + sanitização de nome + `MultipartUploadValidator` 50 MB). Mas o modelo é de *pacote de mapeamento versionado* (spec/gabarito, idempotência por conjunto de hashes, projeto obrigatório). Análise é outro conceito: por usuário, sem revisão. **Reusar o padrão e helpers, não as tabelas.** |
| RBAC de workspace: `[RequireWorkspaceRole]` + `{workspaceId:guid}` na rota; não-membro → 404 fail-closed; `ICurrentUser.UserId` vem do BFF em loopback | `Services/Filters/RequireWorkspaceRoleFilter.cs` | Reusar como está. |

**Conclusão IDS:** não existe armazenamento de arquivo por workspace reaproveitável *como tabela*; existe o **padrão** e os utilitários. Cria-se um par de tabelas novo, fino, no `IdentityDatabase`, e um pequeno serviço de blob espelhando `FiscalPackageService`.

## 2. Decisão de modelo

### 2.1 Entidades (`IdentityDatabase:*`, DDL lazy via `EnsureSchemaAsync`, registrado em `FiscalSchemaInitializer`)

```
tbLpFiscalAnalysis
  AnalysisId      UNIQUEIDENTIFIER PK        -- gerado no servidor
  WorkspaceId     UNIQUEIDENTIFIER NOT NULL  -- FK lógica p/ tbLpFiscalWorkspace
  OwnerUserId     UNIQUEIDENTIFIER NOT NULL  -- ICurrentUser.UserId
  CreatedAt       DATETIME2 DEFAULT SYSUTCDATETIME()
  Source          NVARCHAR(16)   -- 'upload' | 'auto'
  LayoutMode      NVARCHAR(16)   -- 'file' | 'catalog'
  LayoutGuid      NVARCHAR(64) NULL   -- quando o layout veio do catálogo (/auto)
  LayoutName      NVARCHAR(256) NULL  -- rótulo p/ exibição
  DetectedType    NVARCHAR(32) NULL   -- txt/mqseries/idoc (já calculado no fluxo)
  ExpiresAt       DATETIME2 NOT NULL
  IX (WorkspaceId, OwnerUserId, CreatedAt DESC)

tbLpFiscalAnalysisFile
  AnalysisFileId  UNIQUEIDENTIFIER PK
  AnalysisId      FK -> tbLpFiscalAnalysis (ON DELETE CASCADE)
  Role            NVARCHAR(16)   -- 'document' | 'layout'
  OriginalFileName NVARCHAR(260) -- sanitizado (mesmo SanitizeFileName do pacote)
  SizeBytes       BIGINT
  Sha256          CHAR(64)
  MimeSniffed     NVARCHAR(128) NULL
  StoragePath     NVARCHAR(512)  -- relativo à raiz, nunca exposto
```

Regras do layout: se veio arquivo (`/upload`), guarda-se como file com `Role='layout'` (é o "layout usado" do dono); se veio do catálogo (`/auto`), guarda-se **apenas `LayoutGuid`** — o XML descriptografado do catálogo **nunca** é copiado nem devolvido (o próprio `/auto` já documenta: "o XML descriptografado nunca volta ao navegador"). Copiá-lo aqui vazaria propriedade Sysmiddle por um canal novo.

### 2.2 Onde o conteúdo vive — **disco com hash, não `varbinary`**

| Opção | Prós | Contras |
|-------|------|---------|
| A. `varbinary(max)` no `IdentityDatabase` | Transacional, backup único | Arquivos até 50 MB inflam o SQL Docker da VM Ubuntu (recurso pequeno, dependência crítica de identidade/RBAC/pacotes); MQSeries/IDOC crescem rápido; purga vira DELETE pesado + log de transação. |
| **B. Disco (raiz dedicada) + metadado/hash no SQL** ✅ | Mesmo padrão já em produção para artefatos; SQL leve; purga = apagar diretório; backup de blobs independe | Consistência SQL↔disco não é transacional (mitigada: grava disco, depois SQL; falha do SQL → apaga o que gravou; purga também varre órfãos). |
| C. Reusar `tbPackageArtifact` | Zero DDL | Exigiria projeto+pacote+revisão fictícios, idempotência incompatível, mistura retenção/classificação de spec com dado de cliente. Rejeitado. |

Raiz configurável: `ML:FiscalAnalysesPath` (default `MLData\FiscalAnalyses`, espelhando `ML:FiscalMappingPackagesPath`), caminho `{workspaceId}\{analysisId}\{fileId}_{safeName}`; validar com `SafePathResolver.IsInsideBase`. **Hash é integridade, não chave de armazenamento** (sem content-addressing: dedupe entre usuários cruzaria fronteira de workspace e complicaria o direito de exclusão).

### 2.3 Isolamento

- Todas as rotas sob `api/workspaces/{workspaceId:guid}` + `[RequireWorkspaceRole(Owner, FiscalAdmin, Mapper, Reviewer, Operator, Viewer)]` (membro do workspace; não-membro → 404).
- **Isolamento por padrão (coerente com `sessao-usuario-e-artefatos-compartilhados-2026-08-14`):** listagem/detalhe/download filtram `WorkspaceId = rota AND OwnerUserId = ICurrentUser.UserId`. Análise de outro membro → **404** (fail-closed). Visão "ver as do time" (Owner/FiscalAdmin) fica **fora** desta rodada — decisão de produto; é aditiva (query param) sem quebrar o contrato.
- Anônimo (`UserId` nulo) nunca registra nem lê.
- Download só via endpoint autenticado; `StoragePath` nunca aparece na resposta; `Content-Disposition` com nome sanitizado, `application/octet-stream`, `X-Content-Type-Options: nosniff`.
- Nunca logar conteúdo/nome de arquivo do cliente; logar só `AnalysisId`/`WorkspaceId`/tamanho.

### 2.4 Retenção/TTL — **entra**

Entra na v1: `FiscalAnalysisHistoryOptions.RetentionDays` (default **90**, `<=0` cai no default) e `BackgroundService` de purga espelhando `AiUserSessionHistoryCleanupBackgroundService` (apaga linha + diretório; varre órfãos de disco sem linha). Justificativa: dado fiscal de cliente (LGPD: minimização e limitação de armazenamento); sem TTL o disco cresce sem limite e a exposição do dado sensível é indefinida. 90 d (menor que os 180 d do histórico de jobs, que não guarda conteúdo), ajustável. Somado: `DELETE .../analyses/{id}` pelo dono (exclusão sob demanda, LGPD art. 18) — barato porque a purga já implementa a remoção.

## 3. Contrato

### 3.1 Registro — **no próprio upload/parse, opt-in por `workspaceId`, sem quebrar o request**

Nenhum endpoint novo de escrita "explícito" (evita reenviar arquivos de até 50 MB duas vezes). `POST /api/parse/upload` e `POST /api/parse/auto` ganham o campo de form opcional `workspaceId` (GUID). Se presente **e** `ICurrentUser.UserId` for membro (`IIdentityWorkspaceStore.GetWorkspaceIfMemberAsync`), registra a análise. Ausente/inválido/não-membro → **segue sem registrar** (não é erro; retrocompatível).

Sequência: bytes lidos para `byte[]` **antes** do parse (o `IFormFile` não pode ser usado em `Task.Run` após a resposta) → parse normal → `try { registrar } catch { LogWarning }` **aguardado com timeout curto (ex.: 5 s, CTS própria, não `RequestAborted`)**, *não* fire-and-forget: o cliente precisa do `analysisId` na resposta, e um ID devolvido antes da persistência ficaria pendurado se ela falhasse. Custo: 1 escrita em disco + 2 INSERTs (ms). Falha ⇒ resposta normal com `analysisId: null, historyRegistered: false`. Registra-se **após** o parse aceito (não registra 400/422 de arquivo malformado/vazio — não é análise).

Respostas de `/upload` e `/auto` ganham `analysisId` (nullable) e `historyRegistered` (bool) — campos aditivos.

### 3.2 Leitura

`GET /api/workspaces/{workspaceId}/analyses?page=1&pageSize=20` (pageSize máx. 100; `CreatedAt DESC`)
```json
{ "page":1, "pageSize":20, "total":42,
  "items":[{ "analysisId":"…","createdAt":"…","expiresAt":"…","source":"upload",
             "layoutName":"…","layoutGuid":null,"detectedType":"txt",
             "fileCount":2,"totalSizeBytes":12345 }] }
```

`GET /api/workspaces/{workspaceId}/analyses/{analysisId:guid}`
```json
{ "analysisId":"…","workspaceId":"…","createdAt":"…","expiresAt":"…","source":"upload",
  "layout":{ "mode":"file|catalog","layoutGuid":null,"layoutName":"…","fileId":"…|null" },
  "files":[{ "fileId":"…","role":"document|layout","fileName":"…","sizeBytes":1,"sha256":"…",
             "downloadUrl":"/api/workspaces/{ws}/analyses/{id}/files/{fileId}" }] }
```

`GET …/analyses/{analysisId}/files/{fileId}` → stream do arquivo (sha256 conferido na leitura; divergência → 500 com log, sem servir).

`DELETE …/analyses/{analysisId}` → 204 (dono), 404 caso contrário.

Erros: 404 (inexistente, de outro usuário/workspace, ou expirada); anônimo → 404 (fail-closed do filtro).

### 3.3 Resiliência

Dependências: IdentityDatabase (SQL) e disco. Se caírem, o histórico degrada (`analysisId:null`) e a análise não. Leituras com SQL fora → 503 só nesses endpoints. Sem Redis (SQL é fonte da verdade; sem cache na v1). **Regra inviolável:** só `IdentityDatabase:*`; nunca `Database:*`/`172.31.249.51`. O store usa a mesma resolução de connection string de `SqlIdentityWorkspaceStore`/`SqlFiscalPackageStore`.

## 4. Escopo mínimo da próxima rodada (`@lp-backend-dev`)

1. `Models/Entities/Fiscal/FiscalAnalysis*.cs`, `IFiscalAnalysisStore` + `SqlFiscalAnalysisStore` (DDL lazy, registrado em `FiscalSchemaInitializer`, DI no grupo Database/Fiscal).
2. `FiscalAnalysisService` (blobs com `SafePathResolver`, sha256, sanitização de nome, cleanup em falha; `MultipartUploadValidator` para tamanho).
3. `FiscalAnalysesController` (list/detail/download/delete) com `[RequireWorkspaceRole]` e `AuditActionFilter`.
4. Hook opt-in em `ParseController.Upload`/`Auto` (campo `workspaceId`, try/catch + timeout, `analysisId` na resposta).
5. `FiscalAnalysisHistoryOptions` + `BackgroundService` de purga (RetentionDays=90).
6. `@lp-qa`: isolamento owner/workspace (404 cruzado), paginação, falha de persistência não derruba o parse, path traversal no `fileName`, integridade de hash, purga. `@lp-doc`: Swagger/README. Front (React#197) consome `analysisId` de `/upload`/`/auto`.

**Fora da v1:** visão de time por papel, snapshot de parse/candidatos/transformação, dedupe por hash, re-execução a partir do histórico, cache Redis.

## 5. Riscos

| Risco | Mitigação |
|-------|-----------|
| Tamanho/volume (50 MB/arquivo × usuários) esgota disco | Limite do `MultipartUploadValidator`; TTL 90 d; **cota por usuário** como follow-up se o uso real justificar; monitorar tamanho da raiz. |
| LGPD / dado fiscal de cliente em repouso | Minimização (TTL), exclusão sob demanda, isolamento por dono, sem logs de conteúdo, ACL restrita ao serviço na raiz; nunca enviar a LLM em nuvem (`security.md`). Hardening em repouso do host segue pendência registrada. |
| Inconsistência SQL↔disco | Ordem disco→SQL com compensação + varredura de órfãos na purga. |
| Latência extra no `/upload` | Timeout de 5 s e falha aberta; medir p95. |
| Arquivo malicioso armazenado e servido de volta | `octet-stream` + `nosniff`, nome sanitizado, sniff de MIME; XML/ZIP já passam pelo validador (XXE/zip bomb). |
| Confusão com o corpus de aprendizado (`Examples\`) | Raízes e tabelas separadas; histórico do usuário **nunca** alimenta dataset sem promoção explícita. |
| Cliente sem `workspaceId` não gera histórico (silencioso) | `historyRegistered:false` explícito; front sempre envia o workspace ativo. |
