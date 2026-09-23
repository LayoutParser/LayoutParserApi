# ADR — Versionamento da edição manual de artefato TCL/XSLT

- **Data:** 2026-09-10
- **Autor:** @lp-architect (Aria)
- **Issue:** #381 sub-fase 5a · cross-check #226 (226.1 / 226.2) · interage com #229 do React
- **Status:** proposto (aguarda validação do dono)
- **Contexto compartilhado:** `docs/architecture/cross-check-contratos-226-198-react-2026-09-10.md`,
  `docs/architecture/contrato-rbac-erro-diff-mapping-fiscal-2026-09-10.md`
- **Banco:** `IdentityDatabase:*` — **nunca** `172.31.249.51`.

---

## 1. Problema

O front (LayoutParserReact #226) quer um
`PATCH /api/workspaces/{ws}/mapping-drafts/{draftId}/artifacts/{engine}` para editar o
texto do TCL/XSLT (`engine: 'tcl'|'xslt'`, `baseArtifactHash`, `justification?`).

Hoje o artefato **nasce da compilação determinística** (`MappingDraftRuleTranspiler`, via
`POST .../compile`) a partir das **regras estruturadas** (`MappingDraftRule`), e a
`MappingRelease` resultante é idempotente por `(DraftId, RulesSnapshotHash)`. Depois de
`publish` o artefato é **imutável** (`MappingRelease.cs:25`). O invariante do sistema é:

> **artefato = f(regras estruturadas aceitas)** — reproduzível, com provenance nó→regra
> (`MappingTestRunDivergence.RuleId` via `lp:ruleId` embutido pelo transpilador).

Editar o texto do artefato à mão **quebra esse invariante**: o artefato deixa de ser
função das regras, e o diff por regra do Fiscal Test Lab deixa de fechar.

---

## 2. Decisão

### 2.1 Unidade de versão — **nova `MappingRelease` derivada** (confirma a recomendação do cross-check)

A edição manual **não** é revisão de draft (draft não versiona — sem `RevisionNumber`,
sem `RowVersion`; ver `MappingDraft.cs`). É uma **nova `MappingRelease`** criada em
`draft_compiled`, derivada da release-base, com estes campos novos:

| Campo novo em `MappingRelease` | Tipo | Semântica |
|---|---|---|
| `DerivedFromReleaseId` | `Guid?` | Release-base sobre cuja cópia de artefato o humano editou. `null` para releases nascidas de compilação. |
| `ArtifactSource` | `string` | `"compiled"` (default, todas as releases atuais) ou `"manual_edit"`. |
| `ManualEditReason` | `string?` | `justification` do PATCH — obrigatória para `manual_edit`. |
| `ManuallyEditedArtifactKinds` | `string[]` | Quais artefatos da lista `Artifacts` foram tocados à mão (`["xslt"]`, `["tcl","xslt"]`...). Os demais permanecem cópia fiel da base. |

**Identidade da release — `RulesSnapshotHash` deixa de ser suficiente sozinho:**

- Para `ArtifactSource == "compiled"`: identidade continua `(DraftId, RulesSnapshotHash)` —
  idempotência preservada, comportamento atual intacto.
- Para `ArtifactSource == "manual_edit"`: a release **herda** o `RulesSnapshotHash` da
  base (as regras não mudaram!), então a identidade passa a ser
  `(DraftId, RulesSnapshotHash, ArtifactContentHash)`, onde `ArtifactContentHash` é o hash
  do conjunto de conteúdos de artefato pós-edição. Duas edições manuais que resultem no
  **mesmo texto** convergem para a mesma release (idempotência mantida num eixo novo);
  textos diferentes = releases diferentes, todas com o mesmo `RulesSnapshotHash`.
- `PreviousPublishedReleaseId` continua com o significado atual (linhagem de publicação).
  `DerivedFromReleaseId` é ortogonal: linhagem de **edição**, não de publicação.

### 2.2 Vínculo artefato ↔ regras após a edição

Depois de `manual_edit`, o artefato editado **não é mais derivável das regras**. O sistema
marca e trata isso assim:

1. **Marcação explícita:** `ArtifactSource == "manual_edit"` +
   `ManuallyEditedArtifactKinds`. O GET da release expõe ambos — o front mostra um selo
   "editado manualmente" e **desabilita o diff-por-regra** para os kinds editados
   (o `lp:ruleId` pode ter sido removido/alterado pelo humano; a provenance nó→regra
   deixa de ser confiável).
2. **Regras ficam "dessincronizadas" e isso é visível:** a release derivada carrega
   `RulesDesynced = true` (campo derivado, não persistido: `ArtifactSource == "manual_edit"`).
   O Fiscal Test Lab ainda roda (XSD + diff canônico contra `expectedXml`) — isso continua
   valendo, porque valida **resultado**, não estrutura interna. O que se perde é só o
   detalhamento "qual regra causou a divergência".
3. **Recompilar depois de uma edição manual — política: NÃO sobrescreve, cria ramo novo.**
   `POST .../compile` sempre produz uma release `compiled` com identidade
   `(DraftId, RulesSnapshotHash)`. Se essa identidade já existe (caso comum: nada mudou nas
   regras), a compilação é no-op idempotente e **devolve a release compiled original** —
   **nunca** toca a release `manual_edit` (identidade diferente por causa do
   `ArtifactContentHash`). Resultado: as duas coexistem. O front escolhe qual promover na
   governança. **Não há sobrescrita silenciosa e não há bloqueio** — só bifurcação
   explícita, coerente com o modelo idempotente atual.
4. Se o humano editou o TCL e **depois** mexe numa regra estruturada e recompila: sai uma
   release `compiled` nova (novo `RulesSnapshotHash`), **sem** as edições manuais. A UI
   deve avisar "esta compilação não inclui suas edições manuais da release X" — a edição
   manual não se propaga para regras. Reaplicar é ação manual do usuário.

### 2.3 Concorrência otimista — reusa o padrão do `PATCH .../rules/{ruleId}`

Confirmado: dá para reusar o mecanismo já implementado em
`MappingDraftsController.UpdateRule` (linhas 205-270).

| Item | `PATCH .../rules/{ruleId}` (hoje) | `PATCH .../artifacts/{engine}` (novo) |
|---|---|---|
| Precondição | `If-Match` header obrigatório | idem — `If-Match: "<base64>"` |
| Falta o header | `428` `{ error }` | idem |
| Header não-base64 | `400` `{ error }` | idem |
| Valor do ETag | base64 do `ROWVERSION` da regra | base64 do **`MappingReleaseArtifact.Hash`** da base para aquele `engine` (o `baseArtifactHash` que o front propôs) |
| Divergência | `412` `{ error, current: <regra atual> }` | `412` `{ error, current: <artefato atual da release-base> }` |
| Sucesso | `200` + corpo com `eTag` novo | `200` + corpo da **release derivada** com `eTag` = hash do artefato novo |

Diferença conceitual importante: no PATCH de regra, o `200` **muta a regra in-place**
(novo `ROWVERSION`). No PATCH de artefato, o `200` **cria uma release nova** — o recurso
`{draftId}/artifacts/{engine}` é uma projeção "a edição corrente do artefato do engine no
contexto do draft", e escrevê-lo materializa uma `MappingRelease` `manual_edit`. O
`If-Match` casa contra o hash do artefato **da release que o front tinha em mãos**
(normalmente a última `compiled` ou a última `manual_edit` daquele draft/engine).

### 2.4 RBAC

Hoje edição de regra checa **só membership** (sem `[RequireWorkspaceRole]`). O editor de
artefato é operação de **maior impacto** (produz release candidata) e precisa de gate de
papel:

```csharp
[RequireWorkspaceRole(WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner)]
[HttpPatch("mapping-drafts/{draftId:guid}/artifacts/{engine}")]
```

- **`mapper`** = autor, pode editar (suposição do front — razoável e coerente com a
  convenção existente: approve=reviewer/fiscal_admin, publish=fiscal_admin/owner).
- **`fiscal_admin` / `owner`** = override.
- `reviewer` / `operator` / `viewer` → `403` (via `RequireWorkspaceRoleFilter`).
- Sem membership → `404` fail-closed. Sem identidade → `404` (não `401` — identidade vem
  do BFF; ver `.claude/rules/security.md` e cross-check 226.4).
- **Recomendação adicional (fora do escopo de 5b, mas registrar):** aplicar o mesmo
  `[RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)]` no `PATCH .../rules/{ruleId}` e no
  `POST .../compile`, hoje só protegidos por membership. Decisão de produto — sinalizada
  em §5.

### 2.5 Interação com #229 do React e com a governança existente

- **#229 ("edição manual = revisão candidata, não sobrescreve release publicada"):**
  totalmente compatível. A release derivada **nasce em `draft_compiled`**, nunca em
  `published`/`approved`. Não toca a release publicada corrente. Para entrar em produção
  ela percorre o **mesmo fluxo de governança**: `compile`(implícito no PATCH) →
  Fiscal Test Lab (`test-runs`) → `approve` → `publish`. `publish` da derivada deprecia a
  publicada anterior via `PreviousPublishedReleaseId` (mecânica atual, inalterada).
- **Re-aprovação:** sim, obrigatória. A derivada é uma release nova ⇒ precisa de
  `test_passed` + `approve` (reviewer/fiscal_admin) + `publish` (fiscal_admin/owner).
  Não há atalho "herda a aprovação da base" — o conteúdo do artefato mudou, a aprovação
  anterior não cobre.
- **Trilha:** cada transição da derivada gera `MappingTransition` normalmente. A criação
  da derivada em si (`manual_edit`) registra uma transição sintética
  `null → draft_compiled` com `Justification = ManualEditReason` e `ChecksSnapshot`
  contendo `{ derivedFromReleaseId, manuallyEditedArtifactKinds, baseArtifactHash }`.

### 2.6 Validação de sintaxe (sub-fase 5c) — só referência

O PATCH deve rejeitar TCL/XSLT sintaticamente inválido com **`422`** (convenção do
projeto: `400` só para JSON/base64 malformado; `422` para "entendi o corpo mas o conteúdo
não é aceitável"). O **como** (parser XSLT via `XslCompiledTransform.Load` em sandbox;
lint de TCL) é a sub-fase **5c**, dona `@lp-parser-llm`, e é **gate de 5b** (o endpoint
não faz merge sem ela). Não desenhado aqui.

---

## 3. Ciclo de vida (diagrama)

```
                    POST .../compile
  regras aceitas ─────────────────────────►  Release A  (ArtifactSource=compiled,
  (RulesSnapshotHash = H1)                    id=RA, status=draft_compiled,
                                              identidade=(Draft, H1))
                                                   │
                          front carrega artefato XSLT de RA
                          (baseArtifactHash = RA.Artifacts["xslt"].Hash)
                                                   │
     PATCH .../mapping-drafts/{draftId}/artifacts/xslt
       If-Match: "<base64 do baseArtifactHash>"
       body: { content: "<xslt editado>", justification: "ajuste manual do template X" }
                                                   │
              ┌────────────────────────────────────┼───────────────────────────┐
              │ If-Match diverge do hash atual     │  If-Match confere          │
              ▼                                    ▼                            │
        412 { error, current: <artefato          cria  Release B               │
              atual da release-base> }           ArtifactSource = manual_edit  │
                                                 DerivedFromReleaseId = RA     │
                                                 RulesSnapshotHash   = H1  (herdado) │
                                                 ManuallyEditedArtifactKinds = ["xslt"] │
                                                 identidade = (Draft, H1, ArtifactContentHash) │
                                                 status = draft_compiled       │
                                                 + MappingTransition (null→draft_compiled) │
                                                   │                            │
                          POST .../test-runs  (Fiscal Test Lab: XSD + diff canônico) │
                                                   │  (diff-por-regra desabilitado p/ xslt) │
                                          test_passed / test_failed             │
                                                   │                            │
                                    approve  (reviewer | fiscal_admin)          │
                                                   │                            │
                                    publish  (fiscal_admin | owner)             │
                                                   │                            │
                            Release B = published  ──►  Release A vira deprecated
                                                        (PreviousPublishedReleaseId = RA)

  recompilar (POST .../compile) enquanto B existe e as regras não mudaram:
    → identidade (Draft, H1) já existe → devolve Release A (no-op idempotente).
      Release B (manual_edit) permanece intacta e coexiste.
```

---

## 4. Contrato do `PATCH .../mapping-drafts/{draftId}/artifacts/{engine}`

```
PATCH /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/artifacts/{engine}
Headers: If-Match: "<base64 do baseArtifactHash>"      (obrigatório)
Path:    engine ∈ { "tcl", "xslt" }   ("sysmiddle" → 422, via MappingEngineGuardFilter)
Body:    { "content": "<texto completo do artefato>",
           "justification": "motivo da edição manual" }   // obrigatória
```

| Código | Quando | Corpo |
|---|---|---|
| `200` | edição aplicada — release derivada criada (ou idempotência: mesmo texto → devolve a derivada existente) | release derivada completa + `eTag` = hash do artefato novo |
| `400` | `If-Match` não é base64 · JSON malformado | `{ error }` PT-BR |
| `403` | papel insuficiente (`reviewer`/`operator`/`viewer`) | `{ error }` |
| `404` | sem identidade · sem membership · draft de outro workspace · draft/engine inexistente | — |
| `412` | `If-Match` diverge do hash atual do artefato da release-base | `{ error, current: <artefato atual> }` |
| `422` | `engine` inválido/`sysmiddle` · `content` vazio · `justification` ausente · **sintaxe TCL/XSLT inválida (5c)** | `{ error }` PT-BR |
| `428` | falta `If-Match` | `{ error }` |
| `503` | falha ao persistir | `{ error }` |

**Sem `401` por design** (identidade vem do BFF — cross-check 226.4).

Corpo do `200` (esboço):

```jsonc
{
  "releaseId": "<RB>",
  "draftId": "<draft>",
  "engine": "xslt",
  "status": "draft_compiled",
  "artifactSource": "manual_edit",
  "derivedFromReleaseId": "<RA>",
  "rulesSnapshotHash": "<H1>",              // herdado da base
  "manuallyEditedArtifactKinds": ["xslt"],
  "manualEditReason": "ajuste manual do template X",
  "rulesDesynced": true,
  "artifacts": [
    { "kind": "xslt", "content": "<...>", "hash": "<novo>", "generatedAt": "..." },
    { "kind": "tcl",  "content": "<cópia fiel da base>", "hash": "<igual RA>", "generatedAt": "..." }
  ],
  "eTag": "<novo hash do xslt>"
}
```

---

## 5. O que fica para as sub-issues

- **5b — endpoint + store + concorrência** (@lp-backend-dev):
  - Campos novos em `MappingRelease` (`DerivedFromReleaseId`, `ArtifactSource`,
    `ManualEditReason`, `ManuallyEditedArtifactKinds`) + colunas via `EnsureSchemaAsync`
    idempotente no `IdentityDatabase` (`ADD COLUMN`, nada de `CREATE TABLE`, nada em
    `172.31.249.51`).
  - `IMappingReleaseStore.CreateManualEditReleaseAsync(draftId, engine, content, baseArtifactHash, reason, actor)` — valida `If-Match` contra o hash do artefato da
    release-base, calcula `ArtifactContentHash`, aplica idempotência no eixo novo, grava
    release + `MappingTransition` sintética.
  - Controller: reusa o esqueleto de `UpdateRule` (428/400/412/200) + `[RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)]`.
  - Guarda `MappingEngineGuardFilter` para barrar `sysmiddle`.
- **5c — validação de sintaxe** (@lp-parser-llm): gate de 5b; `422` em TCL/XSLT inválido.
- **Governança (já existe):** nenhum código novo — a release derivada usa
  `approve`/`publish`/`rollback`/`deprecate`/`archive` como estão.
- **Decisão de produto pendente (§5 perguntas):** aplicar `[RequireWorkspaceRole]` também
  em `PATCH .../rules/{ruleId}` e `POST .../compile`.

---

## 6. Perguntas em aberto para o dono

1. **Recompilar após edição manual:** o ADR opta por **coexistência** (compiled e
   manual_edit lado a lado, sem sobrescrita nem bloqueio). Confirma? Alternativa seria
   bloquear `compile` enquanto houver `manual_edit` não publicada no draft.
2. **`mapper` pode editar artefato?** O front supõe que sim. Confirma, ou edição de
   artefato (mais sensível que edição de regra) deve exigir `fiscal_admin`+?
3. **Reforçar RBAC de regra/compile** (`[RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)]`
   onde hoje só há membership) — fazer junto com #381 ou issue separada?
4. **Editar os dois engines (`tcl` e `xslt`) numa release derivada:** dois PATCHes
   sequenciais (cada um deriva do anterior) ou um PATCH com array de artefatos? O ADR
   assume **um engine por PATCH**, encadeando.
