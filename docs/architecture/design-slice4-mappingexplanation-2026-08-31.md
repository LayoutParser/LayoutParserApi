# Design — Slice 4: `MappingExplanation` (issues #226/#227)

> Autora: `@lp-architect` (Aria) · 2026-08-31 · Só design, sem código. Segue §10 do prompt
> original (texto literal recuperado via `git show cd62e53` — o arquivo
> `spec-plataforma-fiscal-prompt-original-2026-08-31.md` está em `develop`, não nesta branch
> feature; não parafraseei, li o diff completo). Depende de Slice 3 (`MappingDraft`, PR local
> já implementado nesta branch) e do `MappingEngineGuardFilter`.

## 0. Esclarecimento decisivo — o que é "explicável" HOJE

O prompt fala de `mappingId`/`version`, não `draftId`. Investigado: **não existe ainda** um
conceito de "mapping compilado e versionado" na API — `MappingRelease`/compilação é Slice 5,
ainda não implementado. Hoje só existem dois universos de artefato explicável:

1. **`MappingDraft` (Slice 3, TCL/XSL/XSLT em construção)** — tem regras estruturadas
   (`MappingDraftRule`), mas **não tem código-fonte TCL/XSL ainda** (isso é Slice 5). Não dá
   para rodar os adapters de parsing de código contra um Draft — só existe a representação
   intermediária, que já É basicamente uma explicação (é literalmente o formato do §8).
2. **Mapper Sysmiddle real, já publicado em produção** — via `MapperDatabaseService`/
   `CachedMapperService`/`ICachedMapperService`, catálogo existente (`tbMapper` via
   `GetAllMappersAsync`, `MapperVo.XslContent`, `LinkMappingItem[]`). Isso **já é** um mapping
   identificável e versionável hoje: `MapperGuid` é o ID estável, e o timestamp/`ValueContent`
   funciona como versão implícita (não há versionamento explícito, é mais um "estado atual").

**Decisão:** o endpoint do Slice 4 serve dois casos, discriminados por `engine`:

- `engine=sysmiddle`: `{mappingId}` = `MapperGuid` do catálogo real (`tbMapper`), `{version}`
  é sempre `"current"` (não há histórico de versão Sysmiddle na API — read-only mesmo pra
  metadado). Fonte: `ICachedMapperService`/`MapperDatabaseService`, já existentes.
- `engine=tcl|xslt`: `{mappingId}` = `draftId` do Slice 3 (ainda não há `MappingId`/
  `MappingRelease` publicados — Slice 5 introduz isso). `{version}` = `"draft"` até o Slice 5
  existir; quando existir, passa a aceitar um número de versão real de `MappingRelease` e o
  adapter troca a fonte (regras do Draft → AST do artefato compilado). **Não adiar o endpoint
  até o Slice 5** — adiantar o contrato agora, apontando pra fonte disponível (Draft), é
  exatamente o valor do Slice 4: dar visibilidade parcial e honesta, com `supportLevel`
  refletindo que ainda não houve compilação (regras `proposed`/`needs_input` viram
  `best_effort` ou `opaque`, nunca `authoritative` antes de `compiled`/`accepted`).

Isso NÃO contradiz o prompt: §10 pede "contrato canônico independente do motor" — não exige
que só mappings publicados sejam explicáveis. Read-only pro Sysmiddle e human-in-the-loop pro
TCL/XSLT continuam intactos.

## 1. Contrato `MappingExplanation` (canônico, `Models/Dtos/Fiscal/MappingExplanation.cs`)

```csharp
public sealed record MappingExplanation(
    string MappingId,
    string Version,               // "current" (sysmiddle) | "draft" | número real (pós-Slice 5)
    string Engine,                // "sysmiddle" | "tcl" | "xslt"
    EngineCapabilities Capabilities,
    SchemaRef? SourceSchema,
    SchemaRef? TargetSchema,
    IReadOnlyList<ExplainedRule> Rules,   // ordem estável = ordem de execução/avaliação
    string? Description,
    IReadOnlyList<string> Limitations,
    int OpaqueRuleCount);

public sealed record ExplainedRule(
    string RuleId,                 // estável entre chamadas (MappingDraftRule.Id ou hash determinístico p/ Sysmiddle/XSLT)
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<string> TargetRefs,
    string? Condition,
    IReadOnlyList<string> Operations,
    string Cardinality,
    IReadOnlyList<EvidenceRef> Evidence,
    string HumanDescription,       // PT-BR, gerada por template, não LLM (determinístico)
    string? TechnicalDetail,       // trecho original (XSLT/DSL/regra) truncado, nunca payload fiscal real
    string SupportLevel);          // authoritative | best_effort | opaque | unsupported

public sealed record EngineCapabilities(bool Execute, bool Explain, bool Author, bool Compile, bool Publish);
```

`SupportLevel` por origem:
- `authoritative`: regra reconhecida 100% pela gramática/AST conhecida (nó XSLT suportado,
  função DSL do catálogo fechado, `MappingDraftRule.Status ∈ {accepted, edited, validated}`).
- `best_effort`: reconhecida mas com heurística (ex.: `MappingDraftRule.Status == proposed`,
  ainda não revisada por humano).
- `opaque`: elemento reconhecido como "existe" mas sem semântica traduzível (função DSL fora do
  catálogo fechado, extensão XSLT desconhecida).
- `unsupported`: elemento fora de qualquer gramática esperada — sinaliza drift no motor.

`GET .../explanation` sempre retorna `200` com o contrato acima mesmo quando 100% `opaque` —
nunca falha por não entender uma regra (§4 do prompt: "nunca inventar regra autoritativa").

## 2. Três adapters — reaproveitamento vs. novo

### 2.1 Adapter Sysmiddle (`SysmiddleExplanationAdapter`) — reaproveita bastante

Fonte: `MapperVo` já parseado por `MapperDatabaseService`/`RealMapperParser` (o parser real,
decifrado em `decisao-dsl-mapper-sysmiddle-2026-08-21.md` — gramática fechada: sentinelas
`%beginRuleContent;`/`%endRuleContent;`, `if(`/`else if(`/`else` com `begin/end`, operador
`=`/`!=` string, dispatcher fechado de funções). Correlaciona `LinkMappingItem` (já resolve
`TargetLeafName`) com o catálogo GUID→XPath existente (`SysmiddleSectionMappingResolver`,
citado em memória como já resolvendo saída). Funções fora do dispatcher fechado (§5 do
documento de decisão) → `opaque`. **Zero capability de autoria** — `EngineCapabilities` é
sempre `{Execute:true, Explain:true, Author:false, Compile:false, Publish:false}`, hard-coded,
nunca lido de config (defesa em profundidade, mesmo espírito do `MappingEngineGuardFilter`).
Novo: o `RealMapperParser` hoje produz `MapperVo`/`LinkMappingItem` (dados de vinculação), não
a árvore de `CodeBlock` do `RuleInterpretor` (condicionais aninhados) — precisa de um parser
**dedicado à explicação**, complementar ao de execução, cobrindo só os 3 condicionais + 4
funções confirmadas na decisão de 2026-08-21. Não reabrir a decompilação; a gramática já está
documentada em prosa, é suficiente para escrever o parser de explicação sem tocar as DLLs de
novo.

### 2.2 Adapter TCL (`TclExplanationAdapter`) — reaproveita a base, mas produz saída diferente

`RealMapperParser`/`XslSynth.Contracts` (`ai/XslSynth.Contracts/Core/RealMapperParser.cs`) hoje
parseia o **MapperVO real** (XML de vinculação + `ContentValue` DSL) para fins de síntese
(`CandidateBuilder`, `DslRuleTranslator`) — não é TCL propriamente, é a DSL Sysmiddle. Para TCL
de fato (issue #103, artefato gerado no Slice 5, ainda não existe hoje): o parser AST real
ainda não existe neste repo — precisa ser criado quando o Slice 5 gerar TCL pela primeira vez.
**Decisão:** o adapter TCL do Slice 4 nasce hoje operando sobre `MappingDraftRule` (Slice 3,
representação intermediária já estruturada — já tem `sourceRefs`/`targetRefs`/`operation`/
`condition`/`evidence`, é quase 1:1 com `ExplainedRule`), e ganha um segundo modo quando o
Slice 5 existir (parsear o TCL gerado de verdade via AST dedicado — não regex, conforme
proibição explícita do prompt). Reaproveita `StructuredRuleSchema`
(`ai/XslSynth.Contracts/Prompting/StructuredRuleSchema.cs`) como forma de validar que o shape
de `MappingDraftRule` já é compatível com o que o LLM produz — reduz tradução.

### 2.3 Adapter XSL/XSLT (`XsltExplanationAdapter`) — parcialmente novo

Não existe hoje nenhum parser de **árvore XSLT** para navegação semântica — `XsdValidationService`
valida XML de saída contra XSD (schema de destino), não analisa a estrutura do XSLT em si.
`XslGeneratorService`/`ImprovedXslGeneratorService` **geram** XSLT (`XElement`/`XDocument`), não
o **leem** de volta para explicação — mas confirma que `System.Xml.Linq` (já usado em todo o
projeto, inclusive no `RealMapperParser`) é a ferramenta certa: XSLT é XML válido, então
`XDocument.Parse` + navegação por `XName` nos elementos do namespace `xsl:` cobre `xsl:template`,
`xsl:value-of/@select`, `xsl:for-each/@select`, `xsl:if/@test`, `xsl:choose/xsl:when/@test`,
variáveis (`xsl:variable/@name`) — todos acessíveis como atributos/elementos padrão via
`XElement.Attribute`/`.Elements()`. **100% novo como funcionalidade (não existe hoje um
"explicador" de XSLT), mas reaproveita 100% a infraestrutura XML já madura do projeto** — não
precisa de biblioteca XSLT nova. Extensões desconhecas (`xsl:` com nome de elemento fora da
lista fechada acima, ou `msxsl:`/funções de extensão) → `opaque`.

## 3. Endpoint

```
GET /api/workspaces/{workspaceId}/mappings/{mappingId}/versions/{version}/explanation
  → 200 MappingExplanation (sempre — nunca falha por conteúdo não reconhecido)
  → 404 fail-closed (sem membership no workspace, OU mappingId/version não resolve pra nenhum
    Draft do workspace nem mapper Sysmiddle visível ao workspace)
```

Roteamento interno por `engine` resolvido a partir do `mappingId`: primeiro tenta resolver como
`draftId` (Slice 3, `IMappingDraftStore`); se não encontrar, tenta como `MapperGuid` Sysmiddle
via `ICachedMapperService`, sempre validando isolamento por workspace nos dois caminhos (mesmo
padrão fail-closed de Slice 1/2/3 — nenhum item de outro workspace revela existência).
Sob `[ServiceFilter(typeof(WorkspaceMembershipFilter))]`. `MappingEngineGuardFilter` **não** se
aplica aqui — é rota de leitura, não de autoria; explicar Sysmiddle é exatamente o caso
permitido (§4 do prompt: Sysmiddle pode `explain`).

## 4. Plano de execução

1. **`@lp-parser-llm` (Lia)** — dono natural dos 3 adapters, é domínio de parsing, não CRUD:
   - `SysmiddleExplanationAdapter`: parser de explicação para `CodeBlock`/condicionais (gramática
     já documentada em `decisao-dsl-mapper-sysmiddle-2026-08-21.md`), sobre `MapperVo` existente.
   - `TclExplanationAdapter`: modo 1 sobre `MappingDraftRule` (mapeamento quase direto).
   - `XsltExplanationAdapter`: navegação `XDocument` sobre XSLT real (usar fixtures de XSLT já
     geradas pelo `RepairOrchestrator`/testes existentes como amostra).
   - Template determinístico de `HumanDescription` em PT-BR (não LLM — sem custo, sem
     variabilidade, sem risco de alucinação numa explicação que deve ser confiável).
2. **`@lp-backend-dev` (Dex)** — depois dos adapters prontos:
   - `Models/Dtos/Fiscal/MappingExplanation.cs` (contrato §1).
   - `MappingExplanationController` com o endpoint §3, resolução de `mappingId` (Draft vs.
     Sysmiddle) e isolamento por workspace.
   - DI dos 3 adapters via interface comum `IMappingExplanationAdapter` (`Scoped`, resolvido por
     `engine` — factory pattern, não `if/else` no controller).
3. **`@lp-qa` (Quinn)** — teste de isolamento cross-workspace nos dois caminhos de resolução,
   teste de `opaque` para função Sysmiddle desconhecida, teste de XSLT com extensão não
   reconhecida, teste garantindo `Capabilities.Author == false` para `engine=sysmiddle` em toda
   resposta (regressão do princípio central do slice).

**Primeiro passo executável:** `@lp-parser-llm` — implementar `SysmiddleExplanationAdapter`
primeiro (é o que tem gramática mais bem documentada e already-decifrada, menor incerteza),
sobre `MapperVo`/`RealMapperParser` já existentes. Nenhum código escrito nesta sessão — design
puro.

## 5. Implementação — 2026-08-31 (`@lp-parser-llm`)

Slice 4 implementado ponta a ponta nesta sessão (adapters + contrato + endpoint + testes —
escopo estendido além do plano original do §4, que previa a divisão entre Lia/Dex/Quinn):

- **Contrato** `Models/Dtos/Fiscal/MappingExplanation.cs` — exatamente o shape do §1
  (`MappingExplanation`/`ExplainedRule`/`EngineCapabilities`/`EvidenceRef`/`SchemaRef` como
  `record`), mais `MappingExplanationSupportLevel` (`static class`, mesmo padrão de
  `MappingDraftRuleStatus`).
- **`SysmiddleExplanationAdapter`** (`Services/Fiscal/`) — reaproveitou
  `XslSynth.Core.DslStructuredParser` (Camada 0 do RAG) em vez de escrever um parser de
  explicação do zero: `StructuredRule`/`StructuredBranch` já é literalmente uma árvore de
  explicação (condição/origem/destino/funções por ramo), então virou tradução de campo, não
  parsing novo. Catálogo fechado de 4 funções (`GetLength`, `GetValueFromContext`,
  `GetDictionaryValuesFromElement`, `GetSumElementValuesFunction`) — qualquer função fora
  disso vira `opaque` por ramo. `Capabilities.Author` hard-coded `false` em `static readonly
  EngineCapabilities`, sem leitura de config em nenhum caminho. `LinkMappingItem` (mapeamento
  direto) sempre `authoritative`.
- **`TclExplanationAdapter`** — tradução quase 1:1 de `MappingDraftRuleDetail` (Slice 3) para
  `ExplainedRule`, como previsto no design. `SupportLevel` derivado do `Status` humano
  (`accepted/edited/validated` → `authoritative`; `proposed` → `best_effort`; `needs_input` →
  `opaque`; `rejected/superseded` → `unsupported`) — nunca autoritativo antes de revisão
  humana, conforme §1.
- **`XsltExplanationAdapter`** — `ExplainAsync` sempre retorna `unsupported` com `limitations`
  quando chamado (hoje NUNCA há artefato XSLT compilado associado a um Draft — Slice 5
  introduz isso), confirmando a hipótese do design §2.3. O parser real
  (`ExplainXsltDocument`, público/estático) foi escrito e testado isoladamente: navega
  `xsl:template/value-of/for-each/if/choose/when/variable` via `System.Xml.Linq`; elementos
  fora dessa lista fechada (inclusive outro namespace, ex. `msxsl:`) viram `opaque`. Fica
  pronto para ser ligado a uma fonte real assim que o Slice 5 existir.
- **Endpoint** `GET /api/workspaces/{workspaceId}/mappings/{mappingId}/versions/{version}/explanation`
  (`Controllers/MappingExplanationController.cs`) — resolve `engine` tentando `mappingId` como
  `draftId` primeiro (`IMappingDraftStore.GetDraftIfMemberAsync`, decide tcl/xslt pelo
  `draft.Engine`), senão trata como `MapperGuid` Sysmiddle. **Decisão sobre
  `MappingEngineGuardFilter`:** NÃO aplicado — é rota de leitura pura e o próprio caso
  permitido pelo filtro (`engine=sysmiddle` só pode `explain`, nunca `author`) é exatamente o
  que este endpoint faz; aplicar o filtro aqui bloquearia o caso de uso central do slice.
  `WorkspaceMembershipFilter` citado no design ainda não existe no código — a checagem de
  membership ficou inline no controller, mesmo padrão já usado em `MappingDraftsController`.
- **DI**: os 3 adapters registrados como `IMappingExplanationAdapter` (`AddScoped`, 3
  registros), resolvidos no controller via `IEnumerable<IMappingExplanationAdapter>` +
  `.Single(a => a.Engine == "...")` — factory por `Engine`, sem `if/else` cego.
- **Testes** (`tests/LayoutParserApi.Tests/Fiscal/MappingExplanationAdaptersTests.cs`, 10
  testes, unitários sobre os adapters — sem HTTP): os 5 obrigatórios do prompt (`Author=false`
  sempre; função desconhecida → `opaque`; mapeamento TCL correto; XSL sem artefato →
  `unsupported` + `limitations`; isolamento cross-workspace em TCL e XSLT) mais 3 extras
  (função conhecida → `authoritative`; `MapperGuid` inexistente → `null`/404; regra `proposed`
  → `best_effort`).

**Resultado:** `dotnet build` da solução inteira — 0 erros. `dotnet test` da solução inteira —
551 testes, 0 falhas (492 em `LayoutParserApi.Tests` + 59 em `XslSynth.Core.Tests`, incluindo
os 10 novos deste slice).

**Não implementado nesta sessão (fora de escopo, conforme instrução):** Slice 5
(compilação/Test Lab), parser AST de TCL real de execução, qualquer mudança de front-end.
