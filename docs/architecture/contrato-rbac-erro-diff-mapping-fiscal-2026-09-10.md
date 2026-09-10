# Contrato atual: RBAC fiscal, vocabulário de erro otimista e diff estruturado

Issue #376 · Data: 2026-09-10 · Autor: @lp-doc (Duda)
Fonte de verdade: [`cross-check-contratos-226-198-react-2026-09-10.md`](cross-check-contratos-226-198-react-2026-09-10.md)
(itens que o cross-check #226/#198 concluiu que "fecham só documentando").

> **🇧🇷** Documento de referência para o time [LayoutParserReact](../../README.md#2-ecossistema-de-projetos--project-ecosystem).
> Descreve **o que a API já faz hoje** (com ponteiro para arquivo/linha), não roadmap. Onde
> uma capacidade for nova, está marcado como **[NÃO EXISTE — roadmap]**.
>
> **🇺🇸** Reference document for the LayoutParserReact team. Describes **what the API does today**
> (with file/line pointers), not roadmap. New capabilities are flagged **[NÃO EXISTE — roadmap]**.

---

## 1. Matriz de RBAC dos endpoints de mapping fiscal / RBAC matrix

**🇧🇷** Os papéis de workspace são constantes em
[`Models/Entities/Identity/WorkspaceMembership.cs:9`](../../Models/Entities/Identity/WorkspaceMembership.cs)
(`static class WorkspaceRole`):

`owner` · `fiscal_admin` · `mapper` · `reviewer` · `operator` · `viewer`

**🇺🇸** Workspace roles are string constants in `WorkspaceRole` at the path above.

### Mecanismo de enforcement / Enforcement mechanism

| Aspecto | Detalhe | Evidência |
|---|---|---|
| Gate de papel | Atributo `[RequireWorkspaceRole(...)]` | [`Services/Filters/RequireWorkspaceRoleFilter.cs`](../../Services/Filters/RequireWorkspaceRoleFilter.cs) |
| Sem identidade (`ICurrentUser.UserId` nulo) | **404** fail-closed | `RequireWorkspaceRoleFilter.cs:51` |
| Sem membership no workspace da rota | **404** fail-closed (indistinguível de "não existe") | `RequireWorkspaceRoleFilter.cs:65` |
| Membro, porém papel fora da allowlist | **403** `{ "error": "Papel insuficiente para esta operação." }` | `RequireWorkspaceRoleFilter.cs:72-81` |
| Endpoint **sem** `[RequireWorkspaceRole]` | Só exige membership; qualquer papel serve. Não-membro → **404** | `GetDraftIfMemberAsync` / `GetWorkspaceForMemberAsync` nos controllers |

> **Não existe `401`.** A API não autentica ninguém diretamente — identidade vem do BFF via
> `TrustedIdentityMiddleware` ([§11.1 do README](../../README.md#111-identidade-e-autenticação-bff--api)).
> Ausência de identidade → **404**, nunca 401. Ver [`.claude/rules/security.md`](../../.claude/rules/security.md).

### Tabela endpoint × papel exigido / Endpoint × required role

Controllers: [`MappingGovernanceController.cs`](../../Controllers/MappingGovernanceController.cs),
[`MappingDraftsController.cs`](../../Controllers/MappingDraftsController.cs),
[`MappingCompilationController.cs`](../../Controllers/MappingCompilationController.cs).

| Método + rota | Papel exigido | Gate | Evidência |
|---|---|---|---|
| `GET /api/workspaces/{ws}/mapping-releases` | **qualquer** papel (`owner`, `fiscal_admin`, `mapper`, `reviewer`, `operator`, `viewer`) | `[RequireWorkspaceRole(todos os 6)]` | `MappingGovernanceController.cs:51-52` |
| `POST /api/workspaces/{ws}/mapping-releases/{id}/approve` | `reviewer` \| `fiscal_admin` | `[RequireWorkspaceRole]` | `MappingGovernanceController.cs:73-74` |
| `POST /api/workspaces/{ws}/mapping-releases/{id}/publish` | `fiscal_admin` \| `owner` | `[RequireWorkspaceRole]` | `MappingGovernanceController.cs:100-101` |
| `POST /api/workspaces/{ws}/mapping-releases/{id}/rollback` | `fiscal_admin` \| `owner` | `[RequireWorkspaceRole]` | `MappingGovernanceController.cs:126-127` |
| `POST /api/workspaces/{ws}/mapping-packages/{id}/drafts` | só membership (qualquer papel) | — | `MappingDraftsController.cs:59-84` |
| `GET /api/workspaces/{ws}/mapping-drafts/{id}` | só membership | — | `MappingDraftsController.cs:115` |
| `POST /api/workspaces/{ws}/mapping-drafts/{id}/suggestions` (+ `GET`/`DELETE` do job) | só membership | — | `MappingDraftsController.cs:139,165,184` |
| `PATCH /api/workspaces/{ws}/mapping-drafts/{id}/rules/{ruleId}` | só membership | — | `MappingDraftsController.cs:205` |
| `POST /api/workspaces/{ws}/mapping-drafts/{id}/compile` (+ `GET` do job) | só membership | — | `MappingCompilationController.cs:52,83` |
| `GET /api/workspaces/{ws}/mapping-drafts/{id}/releases/{releaseId}` | só membership | — | `MappingCompilationController.cs:101` |
| `POST /api/workspaces/{ws}/mapping-drafts/{id}/test-runs` (+ `GET` do job) | só membership | — | `MappingCompilationController.cs:120,162` |

**Resumo / Summary:**

- `approve` = `reviewer | fiscal_admin`
- `publish` / `rollback` = `fiscal_admin | owner`
- `List` (mapping-releases) = **todos** os membros
- **draft / compile / test-run / edição de regra (`PATCH .../rules/{ruleId}`)** = só membership,
  **sem gate de papel**. A suposição do front "`mapper` = autor, `fiscal_admin`/`owner` = override"
  ainda **não está codificada** (é decisão de produto — ver cross-check §226.3).
- Não-membro → **404**; membro com papel insuficiente em endpoint com gate → **403**.

---

## 2. Vocabulário de erro do padrão `PATCH .../mapping-drafts/{draftId}/rules/{ruleId}`

**🇧🇷** Handler: `MappingDraftsController.UpdateRule`
([`Controllers/MappingDraftsController.cs:205-260`](../../Controllers/MappingDraftsController.cs)).
Concorrência otimista greenfield: `If-Match` obrigatório, ETag = base64 do `ROWVERSION` da regra.
O front vai **reusar este vocabulário** no futuro editor de artefato (#226.1), que ainda **[NÃO
EXISTE — roadmap]**.

**🇺🇸** Handler above. Optimistic concurrency: `If-Match` required, ETag = base64 of the rule's
`ROWVERSION`. The front will reuse this vocabulary in the future artifact editor (#226.1), which
does **not exist yet**.

| Código | Quando / When | Shape do corpo | Evidência |
|---|---|---|---|
| **200 OK** | Regra atualizada | Objeto da regra, incl. novo `eTag` (base64 do novo `ROWVERSION`) no corpo | `MappingDraftsController.cs:258,291-307` |
| **412 Precondition Failed** | `If-Match` não bate com o `ROWVERSION` atual | `{ "error": "...", "current": <regra atual, mesmo shape do 200> }` | `MappingDraftsController.cs:253-257` |
| **428 Precondition Required** | Header `If-Match` ausente ou vazio | `{ "error": "Header If-Match é obrigatório para editar uma regra." }` | `MappingDraftsController.cs:211-212` |
| **400 Bad Request** | `If-Match` presente mas não é base64 válido | `{ "error": "Header If-Match inválido (esperado base64 do ETag)." }` | `MappingDraftsController.cs:219-222` |
| **422 Unprocessable Entity** | Semântica inválida: `status` ausente/desconhecido, ou `justification` faltando para `rejected`/`edited` | `{ "error": "<detalhe>" }` | `MappingDraftsController.cs:225-230` |
| **404 Not Found** | Sem identidade, sem membership, draft de outro workspace, ou regra inexistente | corpo vazio | `MappingDraftsController.cs:208-209,233-235,252` |
| **503 Service Unavailable** | Falha transitória no store | `{ "error": "Não foi possível atualizar a regra no momento." }` | `MappingDraftsController.cs:244-248` |

### Regras explícitas / Explicit rules

- **Não existe `401`.** "Não autenticado" → **404** fail-closed (ver seção 1). O contrato do front
  deve trocar qualquer `401` previsto por `404`.
- **Não existe `403` neste endpoint hoje.** O `PATCH .../rules/{ruleId}` **não tem**
  `[RequireWorkspaceRole]` — autorização é só por membership, e a falha é **404**, não 403.
  O `403` faz parte do vocabulário **do padrão** (emitido pelo `RequireWorkspaceRoleFilter` nos
  endpoints de governança), e o editor de artefato de #226.1 provavelmente ganhará um gate de
  papel — mas isso é implementação nova.
- **`400` vs `422`:** `400` só para entrada **malformada** (base64/JSON inválido); `422` para
  "entendi o corpo, mas o conteúdo não é aceitável" (semântica). Convenção uniforme na fundação
  fiscal — recomendada também para "sintaxe TCL/XSLT inválida" quando o editor de artefato existir.
- **ETag:** devolvido **no corpo** da resposta (campo `eTag`), não em header HTTP. O cliente
  reenvia esse valor em `If-Match` (com ou sem aspas — o handler faz `Trim('"')`).

---

## 3. Shape do diff estruturado que já existe / Existing structured diff

**🇧🇷** O diff **já é estruturado** (não textual) e **já é exposto** pela API — no contexto de
test-run, dentro do corpo de
`GET /api/workspaces/{ws}/mapping-drafts/{draftId}/releases/{releaseId}`, campo `testRunSummary`.
O front pode consumir **já**.

**🇺🇸** The diff is **already structured** (not textual) and **already exposed** by the API —
inside the `testRunSummary` field of the `GET .../releases/{releaseId}` response body. The front
can consume it **today**.

### Cadeia de tipos / Type chain

| Tipo | Campos | Evidência |
|---|---|---|
| `NodeDiff` (núcleo do diff canônico) | `Kind`, `XPath`, `Expected?`, `Actual?` | [`ai/XslSynth.Core/Core/CanonicalDiffer.cs:6`](../../ai/XslSynth.Core/Core/CanonicalDiffer.cs) |
| `MappingTestRunDivergence` (enriquece cada `NodeDiff` com provenance) | `Kind`, `XPath`, `Expected?`, `Actual?`, `RuleId?`, `SourceRefs?`, `Evidence?` | [`Models/Entities/Fiscal/MappingRelease.cs:51`](../../Models/Entities/Fiscal/MappingRelease.cs) |
| `MappingTestRunSummary` (contém a lista) | `Passed`, `Failed`, `CoveragePercent`, `RequiredGatesPassed`, `XsdValid`, `XsdErrors[]`, `Divergences[]` | `Models/Entities/Fiscal/MappingRelease.cs:61` |

- **`Kind`** (de `CanonicalDiffer`): `"missing"` (falta nó esperado) · `"extra"` (nó a mais) ·
  `"text"` (valor de folha difere) · `"name"` (nome do elemento difere) · `"attr"` (atributo
  ausente/divergente).
- **`XPath`**: canônico, com índice `[n]` quando há irmãos de mesmo nome (`CanonicalDiffer.cs:124-133`).
  Normaliza whitespace, ordem de atributos e namespaces antes de comparar.
- **`RuleId` + `SourceRefs` + `Evidence`**: provenance nó → regra de origem (via atributo `ruleId`
  embutido pelo transpilador) → campo/posição de origem. `null` quando o nó divergente não
  rastreia até uma regra.

### JSON de exemplo / Example JSON

Trecho do corpo de `GET .../mapping-drafts/{draftId}/releases/{releaseId}`
(`MappingCompilationController.cs:187-202` monta a resposta):

```json
{
  "releaseId": "8f2c…",
  "draftId": "1a3d…",
  "engine": "xslt",
  "status": "test_failed",
  "artifacts": [ { "kind": "xslt", "content": "…", "hash": "…", "generatedAt": "2026-09-10T12:00:00Z" } ],
  "compileDiagnostics": [],
  "rulesSnapshotHash": "…",
  "testRunSummary": {
    "passed": 3,
    "failed": 1,
    "coveragePercent": 87.5,
    "requiredGatesPassed": false,
    "xsdValid": true,
    "xsdErrors": [],
    "divergences": [
      {
        "kind": "text",
        "xpath": "/NFe/infNFe/emit/CNPJ",
        "expected": "12345678000199",
        "actual": "12345678000100",
        "ruleId": "b7e1…",
        "sourceRefs": ["registro01.campo_cnpj_emitente"],
        "evidence": [
          { "kind": "spreadsheet_row", "reference": "Emitente!A12", "detail": "CNPJ do emitente" }
        ]
      }
    ]
  },
  "eTag": "AAAAAAAAB9E="
}
```

> **Nota / Note:** o formato exato de `evidence[]` segue `MappingDraftRuleEvidence` (Slice 3) —
> confirmar os campos vigentes em `Models/Entities/Fiscal/MappingDraft.cs`.

### O que **[NÃO EXISTE — roadmap]**

- Diff **release A × release B** (comparar duas versões publicadas do mesmo draft).
- Diff **agregado por schema/destino** (hoje é lista plana ordenada por XPath; sem agregação por
  `TargetRefs`/elemento de XSD).
- "Cobertura de destinos obrigatórios" (o `coveragePercent` atual é **cobertura de teste da
  fixture**, não cobertura de campos obrigatórios do XSD alvo).

Ver cross-check §198.2 e §198.5.
</content>
</invoke>
