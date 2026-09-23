# ADR — Contrato de Explicabilidade Fiscal (`MappingExplanation`)

> Autora: `@lp-architect` (Aria) · 2026-09-07
> Status: Aceito (documenta contrato já implementado — Slice 4, issues #226/#227, sessão de
> 2026-08-31). Escrito a pedido do `LayoutParserReact#200` (gate de aceite de contratos), que
> precisa de um documento de design formal para revisar, não apenas do código.
> Fonte: `Controllers/MappingExplanationController.cs`, `Models/Dtos/Fiscal/MappingExplanation.cs`,
> `Services/Interfaces/IMappingExplanationAdapter.cs`, os 3 adapters em `Services/Fiscal/`,
> `tests/LayoutParserApi.Tests/Fiscal/MappingExplanationAdaptersTests.cs`, e o design original
> `docs/architecture/design-slice4-mappingexplanation-2026-08-31.md` (§0-§5, mantido — este ADR
> não o substitui, formaliza o contrato dele no formato que o consumidor externo pediu).

---

## 1. Objetivo e escopo

**Objetivo:** garantir que, para qualquer mapeamento fiscal existente na plataforma — publicado
em produção (Sysmiddle) ou em elaboração (Draft TCL/XSLT) — o front-end consiga perguntar
"por que essa transformação faz o que faz" e sempre receber uma resposta estruturada,
determinística e verificável, mesmo quando a resposta é "não sei explicar essa parte".

**O contrato garante:**
- Uma representação canônica de "regra de mapeamento explicada" (`ExplainedRule`), igual
  independente de o motor por trás ser Sysmiddle, TCL ou XSLT.
- Um nível de confiança explícito por regra (`SupportLevel`) — nunca promove uma regra a
  `authoritative` sem gramática/AST reconhecida por trás, nem gera texto de explicação via LLM
  (é 100% template determinístico, ver §3).
- Sinalização de capacidade por engine (`Capabilities`) — em particular, a garantia de que
  `engine=sysmiddle` nunca declara `Author=true`, resolvendo diretamente o gate `#205`
  (Sysmiddle é estritamente read-only).
- Isolamento por workspace idêntico ao resto da plataforma (fail-closed: sem membership ou
  mapping fora do workspace → `404`, nunca `403` que revelaria existência).

**Fora de escopo deste contrato** (não é isso que o endpoint promete):
- Não é um endpoint de execução/teste (isso é Test Lab, fora do Slice 4).
- Não compila nem valida contra XSD — é leitura pura sobre artefato já existente.
- Não gera nem edita regra — mesmo para TCL/XSLT (a autoria vive em `MappingDraftsController`,
  Slice 3; este endpoint só lê e traduz o que já existe).

---

## 2. Endpoint

```
GET /api/workspaces/{workspaceId}/mappings/{mappingId}/versions/{version}/explanation
```

| Resposta | Quando |
|---|---|
| `200 MappingExplanation` | Sempre que `workspaceId`/`mappingId`/`version` resolvem para um artefato visível ao chamador — **mesmo que 100% das regras sejam `opaque`/`unsupported`**. O endpoint nunca falha por não entender o conteúdo. |
| `404` | Fail-closed: (a) chamador sem membership no workspace, OU (b) `mappingId`/`version` não resolve para nenhum Draft do workspace nem mapper Sysmiddle visível. Os dois casos são indistinguíveis na resposta — não vaza existência de recurso de outro workspace. |
| `503` | Falha de infraestrutura real ao verificar membership (SQL fora do ar) — degrada, não derruba a request com 500. |

**Resolução de `engine` a partir de `mappingId`** (o cliente não escolhe o engine explicitamente):
1. Tenta `mappingId` como `draftId` (GUID, Slice 3). Se resolver e pertencer ao workspace, o
   `draft.Engine` (`"tcl"` ou `"xslt"`) decide o adapter.
2. Senão, trata `mappingId` como `MapperGuid` do catálogo Sysmiddle real (`tbMapper`,
   `ICachedMapperService`) — não é escopado por workspace (é catálogo global read-only), só a
   existência do mapper importa.

`version`: `"current"` é o único valor aceito para `engine=sysmiddle` (não há histórico de
versão Sysmiddle na API — read-only mesmo para metadado). `"draft"` é o único valor aceito hoje
para `engine=tcl|xslt` (até o Slice 5/`MappingRelease` introduzir versionamento real — ver §6).

**Guard aplicado:** só isolamento de workspace (inline no controller, mesmo padrão de
`MappingDraftsController`). `MappingEngineGuardFilter` (que bloqueia autoria em `engine=sysmiddle`)
**deliberadamente não é aplicado aqui** — esta rota é o próprio caso permitido pelo filtro
(explicar Sysmiddle é `explain`, nunca `author`); aplicá-lo bloquearia o caso de uso central do
endpoint.

---

## 3. Estrutura de resposta (contrato canônico)

```csharp
public sealed record MappingExplanation(
    string MappingId,             // MapperGuid (sysmiddle) ou draftId (tcl/xslt)
    string Version,                // "current" | "draft" | número real (pós-Slice 5)
    string Engine,                 // "sysmiddle" | "tcl" | "xslt"
    EngineCapabilities Capabilities,
    SchemaRef? SourceSchema,
    SchemaRef? TargetSchema,
    IReadOnlyList<ExplainedRule> Rules,    // ordem estável = ordem de execução/avaliação
    string? Description,
    IReadOnlyList<string> Limitations,     // texto livre, PT-BR, "por que isso está incompleto"
    int OpaqueRuleCount);

public sealed record EngineCapabilities(
    bool Execute, bool Explain, bool Author, bool Compile, bool Publish);

public sealed record SchemaRef(string? LayoutGuid, string? Description);

public sealed record ExplainedRule(
    string RuleId,                 // estável entre chamadas (MappingDraftRule.Id ou hash determinístico)
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<string> TargetRefs,
    string? Condition,
    IReadOnlyList<string> Operations,
    string Cardinality,            // "1:1" | "1:N"
    IReadOnlyList<EvidenceRef> Evidence,
    string HumanDescription,       // PT-BR, template determinístico — NÃO LLM
    string? TechnicalDetail,       // trecho original truncado (400 chars) — nunca payload fiscal real
    string SupportLevel);          // authoritative | best_effort | opaque | unsupported

public sealed record EvidenceRef(string Kind, string Reference);
```

### 3.1 `SupportLevel` — semântica e como cada engine chega em cada valor

| Valor | Significado | Sysmiddle | TCL (Draft) | XSLT |
|---|---|---|---|---|
| `authoritative` | Regra 100% reconhecida pela gramática/AST | `LinkMappingItem` (vinculação direta) sempre; regra DSL só com funções do catálogo fechado (4 funções, ver §4) | `MappingDraftRule.Status ∈ {accepted, edited, validated}` | Nó XSLT da lista fechada (`value-of`, `for-each`, `if`, `choose/when`, `variable`) |
| `best_effort` | Reconhecida mas não revisada por humano | — (Sysmiddle não tem noção de "revisão pendente", é produção) | `Status == proposed` | — (não usado hoje; reservado) |
| `opaque` | Elemento existe mas sem semântica traduzível | Função fora do catálogo fechado; DSL fora do subconjunto reconhecido pelo parser | `Status == needs_input` | Elemento fora do namespace `xsl:` conhecido (ex.: `msxsl:script`) |
| `unsupported` | Fora de qualquer gramática esperada — sinaliza drift | (não usado hoje) | `Status ∈ {rejected, superseded}` | Draft `xslt` sem artefato XSLT compilado associado (situação de hoje: **sempre**, até o Slice 5 existir — ver §6) |

**Garantia central: nunca é gerado por LLM.** `HumanDescription` é montada por template PT-BR
determinístico em cada adapter — mesma entrada sempre produz a mesma saída, sem risco de
alucinação numa explicação que precisa ser confiável para auditoria fiscal.

### 3.2 `Capabilities` por engine — o campo central do gate `#205`

```csharp
// SysmiddleExplanationAdapter — static readonly, hard-coded, nunca lido de config
EngineCapabilities(Execute: true, Explain: true, Author: false, Compile: false, Publish: false)
```

Este valor **nunca varia** — não é parametrizável por payload, config, feature flag ou papel do
usuário. É a garantia técnica que resolve a exigência do `LayoutParserReact#205`
("`engine=sysmiddle` força `author=false`, `compile=false`, `publish=false`"): o front não
precisa (e não deve) reimplementar essa regra — só ler `capabilities.author === false` e
esconder qualquer affordance de edição. `TclExplanationAdapter`/`XsltExplanationAdapter` também
declaram `Capabilities` fixas por adapter (não vêm do Draft) — a distinção real de "pode editar"
para TCL/XSLT vive em `MappingDraftsController` (Slice 3), não neste contrato de leitura.

---

## 4. Os 3 adapters — origem do dado, não reimplementação

| Adapter | Fonte real | O que faz | Cobertura hoje |
|---|---|---|---|
| `SysmiddleExplanationAdapter` | `ICachedMapperService` → `tbMapper` → `RealMapperParser` (MapperVO real) + `DslStructuredParser` (mesma Camada 0 usada no RAG de síntese) | Traduz `LinkMappingItem` (vinculação direta, sempre `authoritative`) e `MapperRule` (DSL, por ramo de decisão) | Catálogo **fechado** de 4 funções conhecidas (`GetLength`, `GetValueFromContext`, `GetDictionaryValuesFromElement`, `GetSumElementValuesFunction`); qualquer função fora disso → `opaque` por ramo, não a regra toda quando há mistura |
| `TclExplanationAdapter` | `IMappingDraftStore` → `MappingDraftRuleDetail` (Slice 3) | Tradução quase 1:1 de campo (`SourceRefs`/`TargetRefs`/`Operation`/`Cardinality`/`Evidence`) — `SupportLevel` derivado do `Status` humano do Draft | Não lê TCL gerado de verdade (esse artefato não existe ainda — Slice 5). Opera sobre a representação intermediária estruturada do Draft |
| `XsltExplanationAdapter` | Hoje: nada (nenhum artefato XSLT compilado associado a Draft existe) | `ExplainXsltDocument(...)` (método público estático) navega `XDocument` real de um XSLT — testado isoladamente e funcional — mas **não tem fonte real para chamar em produção ainda** | `ExplainAsync` sempre retorna `unsupported` + `Limitations` não-vazio até o Slice 5 existir. O parser está pronto e testado, só não está "ligado" |

Todos os 3 nunca lançam exceção para conteúdo não reconhecido — degradam para `opaque`/
`unsupported`. Exceção de infraestrutura real (SQL fora do ar, catálogo indisponível) é a única
que propaga, e o controller a converte em `503`.

---

## 5. Garantias de segurança

1. **Nenhum payload fiscal real (documento do cliente) aparece na resposta.** `TechnicalDetail`
   é sempre o trecho de **código/DSL/configuração da regra** (XSLT, `ContentValue` da DSL
   Sysmiddle), truncado a 400 caracteres — nunca dado de um XML de nota fiscal real processado.
   Isso satisfaz a exigência de #200/#205 ("nenhum payload documental sensível em explicação/log").
2. **`Capabilities.Author=false` hard-coded para Sysmiddle** — não há caminho de config, header,
   nem payload que altere esse valor (§3.2). Coberto por teste automatizado
   (`Sysmiddle_CapabilitiesAuthor_IsAlwaysFalse`).
3. **Isolamento fail-closed por workspace** — `Draft` de outro workspace resolve para `null` no
   adapter (não `403`), e o controller devolve `404` uniforme. Coberto por teste
   (`Tcl_DraftFromOtherWorkspace_ReturnsNull`, `Xslt_DraftFromOtherWorkspace_ReturnsNull`).
   O catálogo Sysmiddle é global read-only por design — não tem noção de workspace, e isso é
   intencional (é o mesmo mapper em produção, visível a quem tiver o `MapperGuid`).
4. **Sem escrita em nenhum caminho.** O endpoint é `[HttpGet]` puro; nenhum dos 3 adapters chama
   qualquer store de escrita. `MappingEngineGuardFilter` não precisa ser aplicado porque não há
   verbo de mutação nesta rota (§2).
5. **Logging não inclui conteúdo de regra/DSL.** Os `LogError`/`LogWarning` nos adapters logam
   `MapperGuid`/`DraftId`/mensagem de exceção — nunca o `ContentValue` decifrado nem o XSLT.

---

## 6. Gap conhecido (não corrigido silenciosamente)

**`engine=xslt` está estruturalmente incompleto hoje, por design, não por bug.** O parser de
navegação XSLT (`XsltExplanationAdapter.ExplainXsltDocument`) existe, foi testado isoladamente
e produz `authoritative`/`opaque` corretamente para um XSLT real — mas nenhuma fonte de XSLT
compilado está ligada a um `Draft` ainda, porque essa compilação é escopo do **Slice 5**
(`MappingRelease`, ainda não implementado). Até lá, `GET .../explanation` para
`engine=xslt` **sempre** retorna `Rules: []`, `OpaqueRuleCount: 0`,
`Limitations: ["..."]` — nunca `authoritative`. Isso é o comportamento correto e intencional
(§0 do design original), mas o front deve tratá-lo como "engine ainda sem dado", não como erro —
não é um estado transitório de um mapping específico, é o estado de **todo** `engine=xslt` até
o Slice 5 existir.

**Versionamento (`version` != `"current"`/`"draft"`) não é aceito ainda.** O campo `Version` no
contrato já reserva espaço para "número real (pós-Slice 5)", mas hoje qualquer valor fora de
`"current"` (sysmiddle) ou `"draft"` (tcl/xslt) resolve para `404` (Sysmiddle) ou não resolve o
draft (tcl/xslt, já que a busca é por `draftId`, não por número de versão). Consumidores não
devem construir URLs com números de versão reais ainda — comportamento correto até o Slice 5.

---

## 7. Versionamento do contrato

Não há hoje um mecanismo formal de versionamento de contrato (sem `Accept: application/vnd...`,
sem prefixo `/v2/` na rota). Isso é aceitável para o estágio atual (contrato novo, um único
consumidor conhecido — o front do LayoutParserReact, ainda atrás de feature boundary) mas precisa
de uma regra explícita antes do `MappingExplanation` sair de trás da feature flag:

**Recomendação (não implementada, decisão em aberto):**
- Campos **aditivos** (novo campo opcional em `ExplainedRule`/`MappingExplanation`) não quebram
  contrato — o front deve ignorar campos desconhecidos, nunca falhar em campo ausente
  presumido "sempre presente".
- Mudança de **semântica** de um campo existente (ex.: o dia em que `Version` passar a aceitar
  número real para `tcl`/`xslt`, no Slice 5) é breaking e deve ser sinalizada por um novo valor
  observável — o campo `Engine`/`Version` já formam a chave que discrimina o "modo" da resposta,
  então o Slice 5 pode introduzir o novo modo sem quebrar `version="draft"` existente
  (o contrato já foi desenhado para isso, §0 do design original: "Draft → AST do artefato
  compilado" é uma troca de fonte, não de shape).
- Remoção de campo, ou mudança de tipo de campo existente, exige nova rota versionada
  (`/v2/...`) — não há histórico disso ainda porque o contrato tem menos de 2 semanas de vida.

---

## 8. Exemplos reais (extraídos do código/testes, não inventados)

### 8.1 `engine=sysmiddle`, regra com função conhecida (`authoritative`)

Entrada (`ContentValue` da regra, formato real da DSL Sysmiddle):
```
%beginRuleContent;T.xMun=GetLength(I.LINHA1/Campo);%endRuleContent;
```

Resposta (`200`):
```json
{
  "mappingId": "MAP_3",
  "version": "current",
  "engine": "sysmiddle",
  "capabilities": { "execute": true, "explain": true, "author": false, "compile": false, "publish": false },
  "sourceSchema": { "layoutGuid": "FLD_IN", "description": "Mapper de teste" },
  "targetSchema": { "layoutGuid": "TAG_OUT", "description": "Descrição" },
  "rules": [
    {
      "ruleId": "ATT_1:0",
      "sourceRefs": ["I.LINHA1/Campo"],
      "targetRefs": ["T.xMun"],
      "condition": null,
      "operations": ["GetLength"],
      "cardinality": "1:1",
      "evidence": [{ "kind": "sysmiddle-rule", "reference": "RegraTeste" }],
      "humanDescription": "Preenche \"xMun\" a partir de I.LINHA1/Campo. Usa a(s) função(ões): GetLength.",
      "technicalDetail": "%beginRuleContent;T.xMun=GetLength(I.LINHA1/Campo);%endRuleContent;",
      "supportLevel": "authoritative"
    }
  ],
  "description": "Descrição",
  "limitations": [],
  "opaqueRuleCount": 0
}
```

### 8.2 `engine=sysmiddle`, função fora do catálogo fechado (`opaque`)

Mesmo shape, mas com `ContentValue` chamando `FuncaoDesconhecidaQualquer(...)` →
`supportLevel: "opaque"`, `humanDescription: "Preenche \"xMun\" a partir de I.LINHA1/Campo. Usa
a(s) função(ões): FuncaoDesconhecidaQualquer."`, `opaqueRuleCount: 1` no nível do envelope.

### 8.3 `engine=tcl`, regra de Draft aceita (`authoritative`)

```json
{
  "mappingId": "5f9c...-draftId",
  "version": "draft",
  "engine": "tcl",
  "rules": [
    {
      "ruleId": "a1b2...-ruleId",
      "sourceRefs": ["I.LINHA1/Campo"],
      "targetRefs": ["T.xMun"],
      "condition": null,
      "operations": ["assign"],
      "cardinality": "1:1",
      "evidence": [{ "kind": "sample", "reference": "linha-42" }],
      "supportLevel": "authoritative"
    }
  ]
}
```
(`Status.Accepted` no `MappingDraftRule` de origem → `authoritative`; se fosse `Proposed`,
`supportLevel` seria `best_effort`.)

### 8.4 `engine=xslt`, sem artefato compilado (`unsupported`, estado atual permanente até Slice 5)

```json
{
  "mappingId": "...-draftId",
  "version": "draft",
  "engine": "xslt",
  "rules": [],
  "limitations": ["Nenhum artefato XSLT compilado associado a este draft ainda — disponível a partir do Slice 5."],
  "opaqueRuleCount": 0
}
```
(Texto de `limitations` ilustrativo do formato — conferir string exata em
`XsltExplanationAdapter` no momento da integração; não fixar esse literal no front.)

---

## 9. Rastreabilidade

- Design original completo (raciocínio §0-§5, decisões de reaproveitamento por adapter):
  [`docs/architecture/design-slice4-mappingexplanation-2026-08-31.md`](design-slice4-mappingexplanation-2026-08-31.md)
- Implementação: `Controllers/MappingExplanationController.cs`,
  `Models/Dtos/Fiscal/MappingExplanation.cs`, `Services/Interfaces/IMappingExplanationAdapter.cs`,
  `Services/Fiscal/{Sysmiddle,Tcl,Xslt}ExplanationAdapter.cs`
- Testes: `tests/LayoutParserApi.Tests/Fiscal/MappingExplanationAdaptersTests.cs` (10 testes —
  os 5 obrigatórios do prompt original + 3 extras)
- Gates que este contrato busca satisfazer: `LayoutParserReact#200` (evidência
  "`MappingExplanation` com fixture TCL/XSLT" e "`capabilities` declara Sysmiddle read-only e
  API nega mutação"), `LayoutParserReact#205` (Sysmiddle estritamente read-only)
