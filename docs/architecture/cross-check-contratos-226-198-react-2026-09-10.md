# Cross-check dos contratos #226 e #198 (time React) × estado da API

Data: 2026-09-10
Autor: @lp-architect (Aria)
Fonte do lado React: contrato detalhado dos bloqueios #226 (editor TCL/XSL/XSLT + RBAC) e #198
(catálogo/ciclo de vida de mappings fiscais), incl. commit `8cff185` (contrato de diff por release).

> **Escopo:** análise de delta. Nada implementado aqui. Legenda:
> **JÁ EXISTE** = disponível hoje, com ponteiro exato · **PARCIAL** = existe base, falta parte ·
> **NÃO EXISTE** = capacidade nova.

---

## Mapa rápido do que a API tem hoje (fundação fiscal, Slices 1–7)

| Recurso | Rota base | Arquivo |
|---|---|---|
| Pacote fiscal + revisões + inventário Excel | `POST/GET /api/workspaces/{ws}/projects/{proj}/mapping-packages`, `.../mapping-packages/{id}/revisions` | `Controllers/FiscalMappingPackagesController.cs` |
| Draft (regras estruturadas human-in-the-loop) | `POST .../mapping-packages/{id}/drafts`, `GET .../mapping-drafts/{id}`, `PATCH .../mapping-drafts/{id}/rules/{ruleId}` | `Controllers/MappingDraftsController.cs` |
| Compilação determinística + Fiscal Test Lab | `POST .../mapping-drafts/{id}/compile`, `POST .../mapping-drafts/{id}/test-runs` | `Controllers/MappingCompilationController.cs` |
| Governança de release | `GET .../mapping-releases`, `POST .../mapping-releases/{id}/approve|publish|rollback` | `Controllers/MappingGovernanceController.cs` |
| RBAC escopado por workspace | atributo `[RequireWorkspaceRole(...)]` | `Services/Filters/RequireWorkspaceRoleFilter.cs` |
| Enum de status do ciclo de vida | `MappingReleaseStatus` (`draft_compiled`/`test_passed`/`test_failed`/`in_review`/`approved`/`published`/`deprecated`/`archived`) | `Models/Entities/Fiscal/MappingRelease.cs:8` |
| Papéis de workspace | `WorkspaceRole` (`owner`/`fiscal_admin`/`mapper`/`reviewer`/`operator`/`viewer`) | `Models/Entities/Identity/WorkspaceMembership.cs:9` |
| Diff canônico node-a-node | `CanonicalDiffer` → `NodeDiff(Kind, XPath, Expected, Actual)` | `ai/XslSynth.Core/Core/CanonicalDiffer.cs` |
| Proveniência de artefato | `ArtifactProvenance` (`synthetic`/`real_customer_sample`) | `Models/Entities/Fiscal/PackageArtifact.cs:56` |

**Conceito-chave que muda quase todas as respostas do #226:** hoje a IA e o humano **nunca
editam TCL/XSLT como texto**. O humano opera sobre **regras estruturadas** (`MappingDraftRule` —
`SourceRefs`/`TargetRefs`/`Operation`/`ConditionsJson`/`TransformationsJson`, ver
`Models/Entities/Fiscal/MappingDraft.cs:68`). O TCL/XSLT só nasce na **compilação determinística**
(`MappingDraftRuleTranspiler`, disparada por `POST .../compile`) e a partir do `publish` fica
**imutável** (`MappingReleaseStatus.Published` — "nenhuma escrita de artefato depois disso; edição
gera nova revisão", `MappingRelease.cs:25`). Não há em lugar nenhum um caminho de mutação de
*conteúdo de artefato*.

---

## #226 — Editor de TCL/XSL/XSLT com RBAC

### 226.1 — `PATCH .../mapping-drafts/{draftId}/artifacts/{engine}` (mutação de conteúdo de artefato)

**NÃO EXISTE.** Capacidade nova.

- O `PATCH` que existe hoje é `mapping-drafts/{draftId}/rules/{ruleId}`
  (`MappingDraftsController.cs:205`) — muta **status/refs/operation de uma regra estruturada**, não
  texto de artefato.
- `MappingReleaseArtifact` (`MappingRelease.cs:41`) é `record` de leitura; o único produtor é
  `IMappingReleaseStore.CreateOrGetCompiledReleaseAsync` (`IMappingReleaseStore.cs:48`), chamado
  só pelo job de compilação. Não há store nem endpoint que aceite `content` novo para um artefato.
- O modelo do front (`engine: 'tcl'|'xslt'`, `baseArtifactHash`, `justification?`) **casa
  conceitualmente** com o que já temos: `Engine` é `"tcl"|"xslt"` (sysmiddle barrado por
  `MappingEngineGuardFilter`); `MappingReleaseArtifact.Hash` serve de `baseArtifactHash`;
  `justification` tem precedente nos endpoints de governança.

**O que falta (implementação nova):**
1. Decidir onde a edição manual vive (ver 226.2) — provavelmente um novo tipo
   `MappingDraftArtifactOverride` ou um campo de conteúdo editável na release pré-publish.
2. Store + serviço de mutação com concorrência otimista (reaproveitar o padrão `ROWVERSION`/ETag
   já usado em regra e release).
3. Validação de sintaxe TCL/XSLT no momento do PATCH (hoje só há validação **de resultado** via
   Fiscal Test Lab, pós-compilação — `IMappingTestRunService`).
4. Recompilar/reconciliar: se o humano edita o TCL à mão, o vínculo "regra → linha de artefato"
   (`MappingTestRunDivergence.RuleId`, `MappingRelease.cs:51`) quebra. Precisa de decisão de
   design: edição manual **descola** o artefato das regras (e o diff por regra deixa de valer) ou
   é um override rastreado.

### 226.2 — edição manual gera nova revisão do draft (pré-compilação) ou nova MappingRelease?

**NÃO EXISTE nenhum dos dois hoje** — mas o modelo de dados favorece a resposta **"nova
MappingRelease"**, não "nova revisão de draft".

- **Draft não tem revisão.** `MappingDraft` (`MappingDraft.cs:42`) não tem `RevisionNumber` nem
  `RowVersion`. Ele referencia uma `RevisionId` **do pacote** (imutável). O que versiona no nível
  do draft são as **regras**, individualmente, via `MappingDraftRule.RowVersion` +
  `MappingDraftRuleDecision` (append-only, `MappingDraft.cs:110`). Não existe "draft v2".
- **Release já é a unidade de versão de artefato.** `IMappingReleaseStore` é idempotente por
  `(DraftId, RulesSnapshotHash)` (`IMappingReleaseStore.cs:45`): cada conjunto distinto de regras
  aceitas gera uma release nova. `publish` congela; nova edição ⇒ nova release
  (`MappingRelease.cs:25`, `MappingGovernanceController.cs:99`). `PreviousPublishedReleaseId`
  encadeia a linhagem.
- **Recomendação de arquitetura:** edição manual de artefato = **nova `MappingRelease`** em
  `draft_compiled` (derivada da anterior, com um marcador de proveniência tipo
  `ArtifactSource = manual_edit` e link para a release-mãe). Editar "a revisão do draft" não tem
  onde encaixar sem inventar versionamento de draft do zero. Se a edição for **antes** da primeira
  compilação, aí sim ela deveria virar edição de regra (fluxo 226.1 não se aplica — não há
  artefato ainda).

### 226.3 — RBAC: qual papel autoriza a mutação? A matriz do front existe na API?

**PARCIAL.**

- **Os 6 papéis existem** e são exatamente os do front:
  `owner | fiscal_admin | mapper | reviewer | operator | viewer` (`WorkspaceMembership.cs:9`).
- **O mecanismo de enforcement existe:** `[RequireWorkspaceRole(...)]`
  (`RequireWorkspaceRoleFilter.cs`) — checa o papel do usuário no `WorkspaceMembership` do
  `{workspaceId}` da rota. Distinção de status já implementada: sem membership → **404**
  (fail-closed, indistinguível de "não existe"); membro com papel insuficiente → **403**.
- **O que os endpoints de governança checam hoje** (`MappingGovernanceController.cs`):
  - `GET mapping-releases` (List, linha 51): **qualquer** papel (`owner`, `fiscal_admin`, `mapper`,
    `reviewer`, `operator`, `viewer`).
  - `approve` (linha 73): `reviewer`, `fiscal_admin`.
  - `publish` (linha 100): `fiscal_admin`, `owner`.
  - `rollback` (linha 126): `fiscal_admin`, `owner`.
- **O que NÃO existe:**
  - Nenhum endpoint de draft/compile tem `[RequireWorkspaceRole]`. `MappingDraftsController`,
    `MappingCompilationController` e `FiscalMappingPackagesController` autorizam **só por
    membership** (`GetDraftIfMemberAsync` / `GetWorkspaceForMemberAsync`) — qualquer membro do
    workspace, independentemente do papel, cria draft, edita regra, compila e roda test-run.
  - Não há `[Authorize]` nem papel para o ato de **editar regra** (`PATCH .../rules/{ruleId}`).
  - Não há mapeamento "`mapper` = autor" em lugar nenhum — `mapper` hoje só aparece na allowlist
    de leitura do List.
  - `ServiceClientRoleMapper` **não existe** no repo (grep vazio). Papel vem de
    `ICurrentUser` + `IIdentityWorkspaceStore.GetWorkspaceIfMemberAsync` (que devolve
    `workspace.Role`). Identidade entra pelo BFF via `TrustedIdentityMiddleware`.
- **Sobre a suposição do front** (`mapper` autor + `fiscal_admin`/`owner` override): é uma
  suposição **razoável e compatível** com a convenção já usada (approve = reviewer/fiscal_admin;
  publish = fiscal_admin/owner), mas **ainda não está codificada**. Aplicar
  `[RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)]` no endpoint novo de 226.1 é trabalho de
  implementação — trivial mecanicamente, mas é decisão de produto qual papel entra.

### 226.4 — contrato de erro (200 / 412 / 428 / 400 / 422 / 401 / 403 / 404)

**PARCIAL — o padrão já é usado, só não neste endpoint (que não existe).**

| Código | A API já emite nesse padrão? | Evidência |
|---|---|---|
| **200 + novo eTag** | SIM (para regra) | `PATCH .../rules/{ruleId}` devolve `Ok(ToRuleResponse(...))` com `eTag` no corpo (`MappingDraftsController.cs:258,306`). Releases também expõem `eTag` (`MappingGovernanceController.cs:164`). |
| **412 com `{ current }`** | SIM (para regra) | `MappingDraftsController.cs:253` — `Status412PreconditionFailed` + `current` = estado atual da regra. Exatamente o shape que o front pede. |
| **428 (falta If-Match)** | SIM (para regra) | `MappingDraftsController.cs:211` — `Status428PreconditionRequired`. |
| **400 (If-Match malformado)** | SIM | `MappingDraftsController.cs:221` — base64 inválido → `BadRequest`. |
| **422 (semântica inválida)** | SIM (amplamente) | `UnprocessableEntity` em todos os controllers fiscais para entrada inválida. Seria o código para "sintaxe TCL/XSLT inválida". |
| **400 vs 422 para sintaxe inválida** | decisão em aberto | Convenção atual do projeto usa **422** para "entendi o corpo mas o conteúdo não é aceitável" — recomendo 422 para erro de sintaxe do artefato, 400 só para JSON/base64 malformado. |
| **401** | NÃO (por design) | Não há `[Authorize]`/401 em lugar nenhum — identidade vem do BFF; ausência de identidade → **404** fail-closed, não 401. O front deve esperar **404**, não 401, para "não autenticado". Ver `.claude/rules/security.md`. |
| **403** | SIM | `RequireWorkspaceRoleFilter.cs:77` — papel insuficiente. |
| **404** | SIM | Padrão uniforme em toda a fundação fiscal. |

**Resumo 226.4:** o único gap real é **onde plugar** esses retornos (o endpoint de 226.1). O
vocabulário de erro já está todo estabelecido e é praticamente idêntico ao que o front desenhou —
**exceto 401**, que o front deve trocar por 404 no contrato dele.

---

## #198 — Catálogo e ciclo de vida

### 198.1 — `archived`: existe endpoint que dispara a transição?

**NÃO EXISTE.** `MappingReleaseStatus.Archived` está declarado (`MappingRelease.cs:32`) mas o
próprio comentário diz *"não usado ainda pelos endpoints deste slice"*. `MappingGovernanceController`
só tem `approve` / `publish` / `rollback`. Não há `archive`, nem `deprecate` manual (deprecação só
acontece como efeito colateral de `publish`/`rollback`). `IMappingReleaseStore` não tem
`ArchiveAsync`. Capacidade nova (endpoint + método de store + transição em `MappingTransition`).

### 198.2 — diff estruturado por schema/destino: só textual hoje? `CanonicalDiffer` produz estruturado? Há endpoint?

**PARCIAL.**

- **O diff já é estruturado, não textual.** `CanonicalDiffer.Diff()` devolve
  `IReadOnlyList<NodeDiff>` com `Kind` (`missing`/`extra`/`text`/`name`/`attr`), `XPath` canônico,
  `Expected`, `Actual` (`ai/XslSynth.Core/Core/CanonicalDiffer.cs:6`). O `ToString()` textual é só
  conveniência de log.
- **Já é exposto — mas só no contexto de test-run, não de "comparar duas releases".** O resultado
  entra em `MappingTestRunSummary.Divergences` como `MappingTestRunDivergence` (`MappingRelease.cs:51`),
  que **enriquece** cada nó divergente com `RuleId` + `SourceRefs` + `Evidence` (provenance
  nó→regra→campo de origem). Vem no corpo de `GET .../mapping-drafts/{id}/releases/{releaseId}`
  (`MappingCompilationController.cs:101`, campo `testRunSummary`).
- **O que o front chama de "diff por release" (commit `8cff185`)** provavelmente é esse
  `testRunSummary.divergences` — vale confirmar com eles se é isso que já consomem.
- **O que NÃO existe:** diff **release A × release B** (comparar duas versões publicadas do mesmo
  draft), e diff **agrupado por schema/destino** (hoje é lista plana de nós, ordenada por XPath;
  não há agregação por `TargetRefs`/elemento de schema). "Diff estruturado por destino obrigatório"
  como o #198 descreve = **camada de agregação nova** sobre o `NodeDiff`/`MappingTestRunDivergence`
  já existente. O dado-fonte está lá; a view não.

### 198.3 — perfil fiscal (`documentType`, `schemaVersion`, `operation`, `jurisdiction`) anexado a release/draft?

**NÃO EXISTE** como entidade estruturada em nenhum nível da fundação fiscal.

- `FiscalProject` (`Models/Entities/Fiscal/FiscalProject.cs`) só tem `ProjectId`, `WorkspaceId`,
  `Name`, `CreatedAt`. Comentário explícito: *"CRUD completo de projeto fica fora de escopo"*.
- `MappingDraft` e `MappingRelease` **não têm** nenhum desses 4 campos. `MappingDraftRule.Operation`
  existe mas é a operação de **transformação da regra** (ex.: `concat`, `lookup`), não `operation`
  fiscal (entrada/saída/devolução).
- `documentType`/`schemaVersion` aparecem no repo só no **pipeline de parse/transformação legado**
  (`Models/TransformationRequest.cs`, `Models/XmlAnalysis/XsdValidationResult.cs`) — desacoplado da
  fundação fiscal, vinculado a análise/projeto de layout, **não** a release/draft.
- `jurisdiction` não existe em lugar nenhum.
- **Delta:** capacidade nova. Precisa de um `FiscalProfile` (value object) anexado a
  `FiscalMappingPackage` ou a `MappingDraft`/`MappingRelease`, com migração de schema no
  `IdentityDatabase` (nunca no `172.31.249.51` — regra de segurança). Decisão de design: perfil
  pertence ao **pacote** (todas as revisões/drafts herdam) ou pode variar por release?

### 198.4 — `GET /mapping-releases` aceita filtro por `status` / `draftId` / `environment`?

**NÃO EXISTE.** `MappingGovernanceController.List` (linha 53) aceita **só** `page` e `pageSize`.
`IMappingReleaseStore.ListByWorkspaceAsync` (`IMappingReleaseStore.cs:68`) tem assinatura
`(workspaceId, page, pageSize)` — sem parâmetros de filtro; ordena fixo por `CreatedAt DESC`.
Adicionar `status`/`draftId`/`environment` é: mudar a assinatura da interface, o SQL do
`SqlMappingReleaseStore`, e os query params do controller. Implementação nova, mas pequena e
bem localizada.

### 198.5 — "cobertura de destinos obrigatórios": é o `coveragePercent` existente ou conceito novo?

**Conceito NOVO — não é o `coveragePercent` atual.**

- `MappingTestRunSummary.CoveragePercent` (`MappingRelease.cs:65`, produzido por
  `MappingTestRunService`) é **cobertura de teste**: quão bem a fixture (`inputXml`/`expectedXml`
  passada ao Fiscal Test Lab) exercitou o artefato. É uma métrica de execução de **um** test-run.
- "Cobertura de **destinos obrigatórios**" = quantos dos campos/elementos **obrigatórios do schema
  de saída** (XSD alvo) têm ao menos uma `MappingDraftRule` aceita cobrindo-os (`TargetRefs`).
  É estático (regras × schema), não dinâmico (execução × fixture). Não é calculado nem exposto
  hoje. Precisa: parser do XSD alvo para enumerar elementos obrigatórios + cruzamento com
  `TargetRefs` das regras `accepted`/`edited` do draft. Capacidade nova.

---

## Resumo executivo

### Dá para fechar só documentando (nenhum código novo)

- **226.3 (papéis)** — os 6 papéis e o mecanismo `[RequireWorkspaceRole]` já existem; a matriz
  approve/publish/rollback já está codificada. Documentar a matriz atual para o front.
- **226.4 (erros)** — o vocabulário 200/412/428/400/422/403/404 já é emitido no `PATCH` de regra
  com shape idêntico ao pedido. **Documentar que 401 não existe** (usar 404 fail-closed) e a
  convenção 400 (malformado) vs 422 (semântica).
- **198.2 (diff)** — o diff **já é estruturado** (`NodeDiff`/`MappingTestRunDivergence` com XPath,
  Kind, provenance por regra) e **já é exposto** em `testRunSummary`. Documentar o shape e
  confirmar com o front se é isso que o commit `8cff185` deles consome. (Só a agregação por
  destino e o diff release×release são novos — ver abaixo.)

### Precisa de implementação nova

| # | Item | Tamanho | Depende de decisão de produto/design |
|---|---|---|---|
| 226.1 | Endpoint de mutação de conteúdo de artefato TCL/XSLT (+ store + validação de sintaxe) | **G** | Sim — edição descola artefato das regras? override rastreado? |
| 226.2 | Modelo de versionamento da edição manual (recomendação: nova `MappingRelease` derivada, com `ArtifactSource`) | **M** | Sim — confirmar "nova release" vs "revisão de draft" |
| 226.3b | `[RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)]` no endpoint novo + (opcional) reforçar papel no `PATCH` de regra e no `compile` | **P** | Sim — qual papel edita |
| 198.1 | Endpoint `POST .../mapping-releases/{id}/archive` + `ArchiveAsync` + transição | **P** | Regras de quando arquivar é permitido |
| 198.3 | `FiscalProfile` (`documentType`/`schemaVersion`/`operation`/`jurisdiction`) anexado a pacote ou release + migração no `IdentityDatabase` | **M** | Sim — perfil no pacote ou na release? |
| 198.4 | Filtros `status`/`draftId`/`environment` em `GET mapping-releases` (interface + SQL + query params) | **P** | Não |
| 198.2b | Agregação de diff por schema/destino + diff release A×release B | **M** | Como agrupar (por `TargetRefs`? por elemento XSD?) |
| 198.5 | Métrica "cobertura de destinos obrigatórios" (parser XSD alvo × `TargetRefs` das regras aceitas) | **M** | Fonte do XSD alvo por perfil fiscal (depende de 198.3) |

### Recomendação de quebra em sub-issues

1. **Doc-only (1 issue, @lp-doc):** "Documentar contrato atual de RBAC fiscal + vocabulário de erro
   otimista + shape do diff estruturado para o time React" — fecha 226.3 (leitura), 226.4, 198.2
   (parte já existente). Desbloqueia o front a começar a UI de leitura sem esperar backend.

2. **#198.4 — filtros de listagem de releases** (1 issue, **P**, sem dependência) — fazer primeiro,
   destrava navegação do catálogo no front.

3. **#198.1 — arquivamento de release** (1 issue, **P**) — completa o ciclo de vida; independente.

4. **#198.3 — perfil fiscal** (1 issue, **M**, decisão de design primeiro) — é pré-requisito de
   198.5 e enriquece o catálogo. Precisa de mini-ADR: perfil no `FiscalMappingPackage`.

5. **#226 — editor de artefato** (épico, 3 sub-issues encadeadas):
   - 5a. ADR: "edição manual de artefato TCL/XSLT — modelo de versionamento e vínculo com regras"
     (@lp-architect) — resolve 226.1/226.2 no papel.
   - 5b. Backend: endpoint `PATCH` + store + concorrência otimista + `[RequireWorkspaceRole]`
     (@lp-backend-dev) — reusa padrão ETag/412/428 já existente.
   - 5c. Backend: validação de sintaxe TCL/XSLT no PATCH (@lp-parser-llm) — precede/gate do 5b.

6. **#198.2b + #198.5 — diff agregado por destino + cobertura de obrigatórios** (1 issue **M**,
   depois de 198.3) — ambos consomem o mesmo parser de XSD alvo; fazer juntos.

**Ordem sugerida:** 1 → 2, 3 (paralelos) → 4 → 6 → 5 (épico, maior, pode correr em paralelo a
partir do 5a).
