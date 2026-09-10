# ADR — Perfil fiscal (`FiscalProfile`) em draft/release

- **Data:** 2026-09-10
- **Autor:** @lp-architect (Aria)
- **Issue:** #379 · cross-check #198.3 · pré-requisito de #380/#198.5
- **Status:** proposto (aguarda validação do dono)
- **Contexto compartilhado:** `docs/architecture/cross-check-contratos-226-198-react-2026-09-10.md`
- **Banco:** `IdentityDatabase:*` — **nunca** `172.31.249.51` (regra de segurança).

---

## 1. Problema

O front (LayoutParserReact #198.3) precisa exibir e filtrar mappings fiscais por
**`documentType`**, **`schemaVersion`**, **`operation`** e **`jurisdiction`**. Hoje nada
disso existe na fundação fiscal:

- `FiscalProject` só tem `Name` + `WorkspaceId` + `CreatedAt`.
- `MappingDraft` / `MappingRelease` não têm nenhum dos 4 campos.
- `MappingDraftRule.Operation` existe mas é a operação de *transformação da regra*
  (`concat`, `lookup`), não `operation` fiscal (entrada/saída/devolução/etc.).
- `documentType`/`schemaVersion` só aparecem no pipeline de parse/transformação **legado**
  (`Models/TransformationRequest.cs`, `Models/XmlAnalysis/XsdValidationResult.cs`),
  desacoplado da fundação fiscal.
- `jurisdiction` não existe em lugar nenhum do repo.

É **pré-requisito de #380/#198.5** ("cobertura de destinos obrigatórios do XSD"): para
enumerar os elementos obrigatórios do schema de saída é preciso resolver **qual XSD é o
alvo**, e isso vem do perfil (`documentType` + `schemaVersion` → entrada em
`XsdValidation:DocumentTypes`).

---

## 2. Decisão

### 2.1 Onde o `FiscalProfile` vive — **na `MappingRelease` (imutável), com origem no `MappingDraft`**

Modelo em duas camadas:

| Camada | Papel | Mutabilidade |
|---|---|---|
| `MappingDraft.FiscalProfile` | Perfil **de trabalho** — o alvo que o autor está mapeando. Editável enquanto o draft não compilou nada. | Mutável (com regra — ver §2.3) |
| `MappingRelease.FiscalProfile` | **Snapshot congelado** copiado do draft no momento da compilação (`CreateOrGetCompiledReleaseAsync`), junto com `RulesSnapshotHash` e `SourceRuleIds`. | Imutável |

**Por que não (a) no `FiscalProject`:** `FiscalProject` é deliberadamente mínimo ("CRUD
completo fica fora de escopo", design-slice2). Um projeto fiscal real agrega **vários**
alvos (NFe 4.00 saída, NFe devolução, CTe...) — amarrar um perfil único ao projeto força
1 projeto por `documentType`+`operation`, o que não corresponde ao uso. Além disso o
perfil no projeto não daria imutabilidade por release "de graça".

**Por que não só (b) no `MappingDraft`:** o draft não versiona (`MappingDraft.cs` não tem
`RevisionNumber` nem `RowVersion`; o que versiona são as regras e as releases). Se o
perfil vivesse *só* no draft, uma release publicada meses atrás perderia a rastreabilidade
de contra qual schema foi validada quando alguém editasse o draft. A #380 (cobertura de
obrigatórios) precisa cruzar **regras aceitas × XSD do perfil** — esse cruzamento tem que
ser reproduzível a partir da release, não do estado atual do draft.

**Por que (c) na `MappingRelease` como snapshot:** casa exatamente com o modelo já
existente — a release **já é** a unidade de versão idempotente por
`(DraftId, RulesSnapshotHash)`, já carrega `SourceRuleIds` e `CompileDiagnostics` como
snapshot. O perfil é mais um campo do mesmo snapshot. A #380 lê `release.FiscalProfile`,
resolve o XSD, enumera obrigatórios, cruza com `release.SourceRuleIds` → 100% reproduzível
e estável.

O draft mantém uma cópia **de trabalho** (`MappingDraft.FiscalProfile`) só para: (1) a UI
ter o que mostrar antes da primeira compilação; (2) a compilação ter de onde copiar.

### 2.2 Imutabilidade

- **`MappingRelease.FiscalProfile` é imutável desde a criação da release** (não só
  pós-`publish`). Segue o mesmo princípio de `RulesSnapshotHash`/`SourceRuleIds`: uma
  release é um snapshot fechado. Mudou o perfil ⇒ recompila ⇒ nova release.
- **`MappingDraft.FiscalProfile` é mutável só enquanto não há release derivada do draft.**
  Depois que existe ao menos uma `MappingRelease` para aquele `DraftId`, mudar o perfil do
  draft é permitido mas **não retroage** às releases existentes (elas guardam o snapshot
  delas). É a mesma semântica de editar uma regra depois de já ter compilado: gera
  divergência que só aparece na *próxima* release.
- Consequência para a #380: a métrica de cobertura é sempre calculada contra
  `release.FiscalProfile`, nunca contra `draft.FiscalProfile`.

### 2.3 Modelo de dados

`FiscalProfile` é um **value object** (sem identidade própria; serializado inline).
Persistência: uma coluna `FiscalProfileJson NVARCHAR(MAX) NULL` em `MappingDraft` e em
`MappingRelease` (mesmo padrão de `ArtifactsJson`/`SourceRuleIdsJson` já usados nos stores
fiscais). Nada de tabela nova — o VO não é consultado relacionalmente; os filtros do
catálogo (#198.4) que precisarem de `documentType`/`operation` podem promover esses dois
campos a colunas indexadas numa iteração futura se a listagem exigir (fora do escopo
deste ADR — hoje a listagem só pagina).

```csharp
namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Perfil fiscal do alvo de mapeamento (issue #379 / cross-check #198.3). Value object —
    /// vive inline no MappingDraft (cópia de trabalho, mutável) e na MappingRelease
    /// (snapshot congelado na compilação, imutável). documentType+schemaVersion resolvem o
    /// XSD alvo via XsdValidation:DocumentTypes (pré-requisito da #380 / #198.5).
    /// </summary>
    public sealed record FiscalProfile(
        string DocumentType,      // enum fechado — chave em XsdValidation:DocumentTypes
        string SchemaVersion,     // string livre — deve casar com XsdVersion da entrada acima
        string Operation,         // enum fechado — FiscalOperation.*
        string Jurisdiction);     // enum semiaberto — FiscalJurisdiction.* (UF ou "BR")

    /// <summary>documentType — fechado, espelha as chaves de XsdValidation:DocumentTypes.</summary>
    public static class FiscalDocumentType
    {
        public const string Nfe = "NFe";
        public const string Cte = "CTe";
        public const string NfCom = "NFCom";
        public const string Mdfe = "MDFe";
        public static readonly IReadOnlyCollection<string> All = new[] { Nfe, Cte, NfCom, Mdfe };
        public static bool IsValid(string? v) => v != null && All.Contains(v);
    }

    /// <summary>operation fiscal — fechado. NÃO confundir com MappingDraftRule.Operation.</summary>
    public static class FiscalOperation
    {
        public const string Outbound = "outbound";        // emissão / saída
        public const string Inbound = "inbound";          // recebimento / entrada
        public const string Return = "return";            // devolução
        public const string Cancellation = "cancellation";
        public const string Complementary = "complementary";
        public static readonly IReadOnlyCollection<string> All =
            new[] { Outbound, Inbound, Return, Cancellation, Complementary };
        public static bool IsValid(string? v) => v != null && All.Contains(v);
    }

    /// <summary>
    /// jurisdiction — semiaberto: as 27 UFs + "BR" (federal / nacional). Validação por
    /// lista fechada de UF, mas o conjunto é notório e estável (não é string livre).
    /// </summary>
    public static class FiscalJurisdiction
    {
        public const string Federal = "BR";
        public static readonly IReadOnlyCollection<string> Ufs = new[]
        { "AC","AL","AP","AM","BA","CE","DF","ES","GO","MA","MT","MS","MG","PA","PB",
          "PR","PE","PI","RJ","RN","RS","RO","RR","SC","SP","SE","TO" };
        public static bool IsValid(string? v) => v == Federal || (v != null && Ufs.Contains(v));
    }
}
```

**Enums fechados vs. string livre — decisão:**

| Campo | Tipo | Racional |
|---|---|---|
| `documentType` | **enum fechado** | Tem que casar 1:1 com uma chave de `XsdValidation:DocumentTypes`. Valor fora da lista = não há XSD = a #380 não roda. Rejeitar na entrada (422). |
| `schemaVersion` | **string livre**, mas **validada por cruzamento** | O valor deve ser igual ao `XsdVersion` da entrada de `documentType` correspondente (ou uma futura lista de versões suportadas por tipo). É livre no *tipo* mas fechado na *validação* — a camada de serviço rejeita (422) se `schemaVersion` não corresponde a um XSD instalado em `XsdValidation:BasePath`. Mantém-se string para acomodar novas NTs sem redeploy de enum. |
| `operation` | **enum fechado** | Conjunto pequeno, estável, semântica fiscal bem definida. |
| `jurisdiction` | **enum semiaberto** (27 UF + `BR`) | Conjunto notório e finito; validar por lista evita lixo, mas não é "enum de código" — é tabela de referência conhecida. |

### 2.4 Cruzamento com `XsdValidation:DocumentTypes`

Hoje `appsettings.json` → `XsdValidation:DocumentTypes` tem 4 entradas (`NFe`, `CTE`,
`NFCom`, `MDFe`), cada uma com `XsdVersion` + `Namespace` + `RootElement`. Nota: a chave é
`CTE` no appsettings mas o `RootElement` é `CTe` — **este ADR adota `CTe`** como valor
canônico de `documentType` e recomenda a sub-issue de implementação **normalizar a chave
do appsettings para `CTe`** (ou mapear no resolver), para o perfil e a config baterem
exatamente.

Regra de validação (camada de serviço, ao gravar o perfil):

1. `FiscalDocumentType.IsValid(documentType)` → senão 422.
2. Existe `XsdValidation:DocumentTypes[documentType]` → senão 422
   ("tipo de documento sem XSD configurado no ambiente").
3. `schemaVersion` == `DocumentTypes[documentType].XsdVersion`
   **ou** o arquivo XSD correspondente existe sob `XsdValidation:BasePath` → senão 422
   ("versão de schema não instalada neste ambiente"). *(A forma exata de resolver o
   caminho do XSD por versão fica para a sub-issue da #380 — aqui basta o contrato: o
   perfil só é aceito se o XSD alvo for resolvível.)*
4. `FiscalOperation.IsValid` / `FiscalJurisdiction.IsValid` → senão 422.

### 2.5 Migração — DDL lazy idempotente no `IdentityDatabase`

Segue o padrão dos outros stores fiscais (`SqlMappingReleaseStore`,
`SqlMappingDraftStore`): bloco `EnsureSchemaAsync` idempotente, disparado no primeiro uso
do store, **só** contra a connection string `IdentityDatabase:*`.

```sql
-- Executado por SqlMappingDraftStore.EnsureSchemaAsync (idempotente)
IF COL_LENGTH('dbo.MappingDrafts', 'FiscalProfileJson') IS NULL
    ALTER TABLE dbo.MappingDrafts ADD FiscalProfileJson NVARCHAR(MAX) NULL;

-- Executado por SqlMappingReleaseStore.EnsureSchemaAsync (idempotente)
IF COL_LENGTH('dbo.MappingReleases', 'FiscalProfileJson') IS NULL
    ALTER TABLE dbo.MappingReleases ADD FiscalProfileJson NVARCHAR(MAX) NULL;
```

- **NULL permitido** — drafts/releases legados não têm perfil. A API trata
  `FiscalProfile == null` como "perfil não definido": o GET devolve `fiscalProfile: null`
  e a #380 devolve cobertura `null`/"perfil ausente" em vez de calcular.
- **Nenhum `CREATE TABLE`** — só `ADD COLUMN`. **Nada** toca `172.31.249.51` /
  `Database:*` (regra `.claude/rules/security.md`).
- Sem índice agora. Se #198.4 (filtros de listagem) precisar filtrar por
  `documentType`/`operation`, promover esses dois a colunas computadas persistidas +
  índice numa sub-issue própria.

### 2.6 Contrato de API

**Entrada — endpoint dedicado, `PUT` idempotente:**

```
PUT /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/fiscal-profile
Body: { "documentType": "NFe", "schemaVersion": "PL_010b_NT2025_002_v1.30",
        "operation": "outbound", "jurisdiction": "SP" }
```

| Código | Quando |
|---|---|
| `200` + perfil salvo | ok (cria ou substitui — idempotente) |
| `422` | qualquer regra de §2.4 violada (mensagem PT-BR específica) |
| `409` | draft já tem release derivada **e** a política escolhida for "travar" (ver nota) |
| `404` | sem identidade / sem membership / draft de outro workspace / draft inexistente |

> **Nota sobre `409`:** §2.2 permite editar o perfil do draft mesmo com release existente
> (não retroage). Portanto o comportamento default é **200** (grava, não retroage). O
> `409` fica reservado caso o dono prefira travar — decisão de produto, sinalizada em §5.

- **Não** embutir o perfil no `POST .../drafts` nem no `POST .../compile`: mantém o
  endpoint de criação enxuto e permite a UI coletar o perfil num passo separado do wizard.
  A compilação apenas **lê** `draft.FiscalProfile` e o copia para a release; se estiver
  `null` no momento do `compile`, a compilação **prossegue** mas a release nasce sem
  perfil e a #380 fica indisponível para ela (warning no `CompileDiagnostics`, não erro).
- RBAC: `[RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)]` — mesmo conjunto proposto
  para o editor de artefato (#381), coerente com "quem mapeia define o alvo".

**Saída — em todo GET de draft e de release:**

```jsonc
// GET .../mapping-drafts/{draftId}
{ "draftId": "...", "engine": "xslt", /* ... */,
  "fiscalProfile": {
    "documentType": "NFe", "schemaVersion": "PL_010b_NT2025_002_v1.30",
    "operation": "outbound", "jurisdiction": "SP",
    "resolvedXsd": { "xsdVersion": "PL_010b_NT2025_002_v1.30",
                     "namespace": "http://www.portalfiscal.inf.br/nfe",
                     "rootElement": "NFe" }   // eco de XsdValidation:DocumentTypes, read-only
  } }

// GET .../mapping-drafts/{draftId}/releases/{releaseId}  → mesmo shape,
// mas fiscalProfile é o snapshot congelado da release (pode divergir do draft atual)
```

- `resolvedXsd` é derivado (não persistido no draft) — conveniência para o front não ter
  que reimplementar o lookup. Na release, `resolvedXsd` é recalculado a partir do snapshot
  (estável, porque `documentType`+`schemaVersion` estão congelados).
- `fiscalProfile: null` quando não definido.

---

## 3. Trade-offs

| Eixo | Escolha | Custo aceito |
|---|---|---|
| Granularidade | Perfil no draft (trabalho) + snapshot na release | 2 lugares para serializar; risco de o front confundir "perfil do draft" com "perfil da release" — mitigado deixando explícito no shape do GET |
| Imutabilidade | Congela na criação da release, não no publish | Uma release em `draft_compiled` não pode ter o perfil "corrigido" sem recompilar — consistente com o resto do snapshot, mas menos flexível |
| Persistência | Coluna JSON, sem tabela, sem índice | Filtro por `documentType` na listagem exige trabalho extra depois (#198.4) |
| `schemaVersion` string livre | Acomoda novas NTs sem redeploy | Validação vira responsabilidade de runtime (arquivo XSD existe?) em vez de compilação |
| Federal vs UF | `jurisdiction` aceita UF **e** `BR` | Front precisa saber que "BR" é federal; documentar |

**Performance:** desprezível — 1 coluna `NVARCHAR(MAX)` lida junto com a linha que já é
carregada; `resolvedXsd` é lookup em dicionário de config em memória. Nenhuma query nova.

**Segurança:** nenhuma exposição de segredo. DDL só no `IdentityDatabase`. Perfil não
contém dado de cliente (é metadado de schema). RBAC reusa filtro existente.

---

## 4. O que fica para as sub-issues de implementação

1. **#379a — modelo + persistência** (@lp-backend-dev): `FiscalProfile` VO + enums;
   coluna `FiscalProfileJson` em `MappingDrafts` e `MappingReleases` via `EnsureSchemaAsync`
   idempotente; serialização nos stores; normalizar chave `CTE`→`CTe` no resolver de
   `XsdValidation:DocumentTypes`.
2. **#379b — endpoint `PUT .../fiscal-profile`** (@lp-backend-dev): validação §2.4 com
   mensagens PT-BR; `[RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)]`; decisão
   200-vs-409 conforme §5.
3. **#379c — snapshot na compilação** (@lp-backend-dev): `CreateOrGetCompiledReleaseAsync`
   copia `draft.FiscalProfile` para a release; warning em `CompileDiagnostics` se `null`.
4. **#379d — saída nos GETs** (@lp-backend-dev + @lp-doc): campo `fiscalProfile` +
   `resolvedXsd` derivado no GET de draft e de release; atualizar Swagger/contrato React.
5. **#380 (já existente)** consome `release.FiscalProfile.resolvedXsd` como fonte do XSD
   alvo — **desbloqueada** por este ADR.
6. **(futuro, se #198.4 exigir)** promover `documentType`/`operation` a colunas indexadas.

---

## 5. Perguntas em aberto para o dono

1. **Editar perfil do draft depois de já existir release derivada:** grava sem retroagir
   (200, default deste ADR) ou trava (409, exige novo draft)?
2. **`schemaVersion`:** validar só "arquivo XSD existe no ambiente" ou manter uma allowlist
   explícita de versões suportadas por `documentType` no `appsettings.json`?
3. **`jurisdiction` é obrigatório?** Para NFe/CTe a UF importa (regras estaduais); para
   um mapeamento puramente estrutural talvez `BR` baste. Campo obrigatório com default
   `BR`, ou nullable?
