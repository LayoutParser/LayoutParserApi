# Design — Slice 5: Compilação TCL/XSL/XSLT + Fiscal Test Lab (issue #231)

> Autora: `@lp-architect` (Aria) · 2026-08-31 · Só design, sem código. Segue Slices 1-4
> (PRs #234/#236/#238/#240, mesclados em `develop`). Releia seções 9 e 11 do
> `spec-plataforma-fiscal-prompt-original-2026-08-31.md` antes de implementar.

## 1. Achados da investigação

- **`RepairOrchestrator` NÃO é reaproveitável por adaptação direta.** Ele parte de `MapperVo`
  (Sysmiddle decifrado) + XSD + XML esperado, e faz um loop **gerar→validar→corrigir com LLM**
  porque a entrada (regras C# do Sysmiddle) é ambígua/incompleta. Aqui a entrada já é
  `MappingDraftRule` **estruturada, tipada e aceita por humano** (`accepted`/`edited`) — não há
  ambiguidade a resolver com IA nesta etapa. Forçar o loop completo (com custo de Ollama
  CPU-only, seção 9: "medir timeout/custo/duração") para uma tradução regra→código
  1:1 é desperdício e reintroduz a variabilidade que o Slice 3 existe pra eliminar.
- **Decisão: geração de XSL/XSLT nesta etapa é DETERMINÍSTICA**, um transpilador regra→XSLT
  (`operation`/`conditions`/`transformations`/`cardinality` → template XSLT), no mesmo espírito
  do `DeterministicXslTranspiler` já usado como baseline dentro do `RepairOrchestrator`
  (`ai/XslSynth.Core`). Reaproveita-se o **padrão de código**, não a classe (assinatura de
  entrada incompatível). IA só reentra se uma regra `accepted` tiver `operation` não coberto
  pelo transpilador — mesmo aí, vira `needs_input` de volta ao Slice 3, não retry silencioso.
- **TCL: mesmo raciocínio.** `TclGeneratorService`/`ImprovedTclGeneratorService` geram TCL a
  partir de spec Excel bruta + aprendizado — pipeline de outra entrada. Necessário um gerador
  novo, determinístico, `MappingDraftRule[] → TCL`, arquitetura irmã do transpilador XSLT (mesmo
  parser de regra, dois back-ends de emissão). Não é fine-tuning nem chamada a Ollama.
- **`CanonicalDiffer` e `XsdValidationService` são diretamente reaproveitáveis**, sem adaptação —
  ambos já operam sobre XML de saída vs. XML gabarito/XSD, agnósticos de como o XML foi gerado.
  É exatamente o papel que a seção 11 pede (validação XSD + diff canônico).
- **`MappingRelease` nasce aqui, mas em estado `Draft`/não publicável.** A seção 6 lista
  `MappingRelease` como filho de `MappingDefinition`; a seção 9 pede "registrar versão e
  artefatos" nesta etapa. A seção 12 (Slice 7) possui o ciclo de vida
  `Draft→InReview→Approved→Published→Deprecated→Archived` e RBAC de promoção — este slice **cria**
  o registro (artefato + versão + resultado de teste), mas não expõe transição para `Approved`/
  `Published`. Ponto de acoplamento explícito para o Slice 7, não reinvenção.

## 2. Modelo de artefato — `MappingRelease` (nasce aqui)

```
MappingRelease
  Id (opaco, imutável)
  DraftId (FK — proveniência até a decisão humana)
  MappingId / Version (dentro de MappingDefinition)
  Engine: tcl | xsl_xslt          // nunca sysmiddle
  Artifacts[] { Kind: tcl|xslt, Content, Hash, GeneratedAt }
  SourceRuleIds[]                  // só accepted/edited, snapshot no momento da compilação
  CompileDiagnostics[] { RuleId, Severity, Message, Position? }
  TestRunSummary { Passed, Failed, CoveragePercent, RequiredGatesPassed: bool }
  Status: draft_compiled | test_passed | test_failed   // NÃO inclui approved/published (Slice 7)
  CorrelationId
  CreatedByJobId
  RowVersion
```

- Idempotência: `compile` com o mesmo `DraftId` + snapshot de regras (hash do conjunto
  `accepted`/`edited`) retorna a `MappingRelease` existente, não duplica.
- `RequiredGatesPassed=false` **bloqueia** qualquer avanço no Slice 7 — o campo é o contrato
  entre os dois slices, evita o Slice 7 reimplementar a checagem.

## 3. `MappingTestCase` / Fiscal Test Lab

Reaproveita `XsdValidationService` (validação estrutural) + `CanonicalDiffer` (diff contra
gabarito) + validações fiscais já existentes (mesmo padrão do pathway de execução atual,
`Services/Transformation`). Executa o artefato (`TCL` via runner Sysmiddle-like determinístico
já existente / `XSLT` via `XsltApplier`, já usado no `RepairOrchestrator`) contra
`MappingTestCase` (fixture individual ou suite versionada), produz:

- cobertura de destinos obrigatórios/opcionais (comparação contra o inventário do `XsdValidator`);
- lista de destinos não mapeados;
- provenance por nó: XML de saída → `MappingDraftRule.Id` → `Evidence[]` (já modelado no Slice 3).

## 4. Endpoints

```
POST /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/compile
  → 202 Accepted, jobId. Fire-and-forget (IServiceScopeFactory, padrão do
    AiTransformationCandidateService). Recusa engine=sysmiddle via MappingEngineGuardFilter
    (já centralizado no Slice 3) — reaproveitar, não duplicar checagem.

POST /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/test-runs
  → 202 Accepted, jobId. Referencia MappingReleaseId (precisa existir e estar compilado).
    Body: { testCaseId | suiteId }.
```

Ambos seguem o padrão ETag/idempotência já estabelecido no Slice 3.

## 5. Divisão de trabalho

Peso de domínio (transpiladores regra→TCL/XSLT, mapeamento de `operation`/`conditions` para
sintaxe alvo) + peso de infra (job assíncrono, endpoints, `MappingRelease`/`MappingTestCase`
no SQL, DI) — **os dois cabem juntos**: `@lp-parser-llm` (Lia) desenha e implementa os dois
transpiladores determinísticos e a integração com `CanonicalDiffer`/`XsdValidationService`;
`@lp-backend-dev` (Dex) implementa controllers, DI, modelo de persistência de
`MappingRelease`/`MappingTestCase`, job assíncrono e `MappingEngineGuardFilter` nas novas rotas.
Não dá para separar em PRs sequenciais sem acoplamento artificial — trabalho em paralelo na
mesma branch, como Slice 3.

## Primeiro passo executável

`@lp-parser-llm`: prototipar o transpilador `MappingDraftRule[] → XSLT` (operações `copy`/
`concat`/`lookup`/`conditional`/`constant` da seção 8) como classe standalone testável,
antes de `@lp-backend-dev` cablear o endpoint — reduz risco de descobrir gaps de cobertura de
`operation` só depois da infra pronta.

## Implementação — transpiladores (2026-08-31)

`Services/Fiscal/MappingDraftRuleTranspiler.cs` — classe estática, sem DI, duas entradas:
`ToXslt(rules, sourceSchema, targetSchema)` e `ToTcl(rules, sourceSchema, targetSchema)`, ambas
retornando `TranspileResult(Content, Diagnostics)`.

- **Filtro de status:** só `accepted`/`edited` viram output; `proposed`/`rejected`/`needs_input`/
  `validated`/`superseded` são ignoradas silenciosamente (reflete decisão humana ainda não tomada
  ou negativa — não é erro).
- **Catálogo suportado:** `copy` (`xsl:value-of` direto / `FIELD op="copy"`), `concat`
  (`concat()` XPath com separador opcional lido de `transformations[0].separator`), `lookup`
  (`xsl:choose`/`when` por chave de `transformations[0].table`, com `default` como `otherwise`),
  `conditional` (`xsl:choose` a partir de `conditions[]`, item com `"default":true` vira
  `otherwise`), `constant` (`xsl:text` fixo de `transformations[0].value`).
- **Contrato JSON livre por operação** (spec §8) documentado nos comentários XML doc de
  `ReadTransformationString`/`ReadLookupTable`/`ReadConditions` — formato mínimo, sem schema
  formal nesta etapa (não pedido no escopo).
- **Rastreabilidade RuleId → elemento gerado:** XSLT usa atributo `lp:ruleId` (namespace
  `urn:layoutparser:provenance`, constante `MappingDraftRuleTranspiler.ProvenanceNamespace`) em
  cada elemento de destino + comentário `<!-- MappingDraftRule {guid} -->` antes dele (canal
  humano complementar). TCL usa atributo `ruleId="..."` direto no `<FIELD>` (sem namespace formal,
  mesmo princípio). Confirmado em teste: dado o XML/TCL gerado, dá pra recuperar o `RuleId` de
  origem de um elemento específico via o atributo.
- **Diagnóstico estruturado, nunca exceção não tratada:** operação fora do catálogo, JSON
  malformado em `conditions`/`transformations`, ou payload incompleto (ex.: `lookup` sem `table`)
  viram `TranspileDiagnostic(RuleId, Severity, Message)` — a regra é omitida do output, as demais
  regras continuam sendo processadas.
- **NÃO reaproveita** `RepairOrchestrator`/`DeterministicXslTranspiler` (só o padrão, conforme
  decisão da seção 1) — assinatura de entrada incompatível (`MapperVo` vs. `MappingDraftRule`).

Testes: `tests/LayoutParserApi.Tests/Services/Fiscal/MappingDraftRuleTranspilerTests.cs`, 16
casos — uma fixture por operação (XSLT e TCL), regra `proposed`/`rejected`/`needs_input` ignorada,
operação não suportada gera diagnóstico sem exceção, rastreabilidade RuleId→elemento. Fixtures
sintéticas com campos fiscais (CNPJ/CFOP), sem dado real.

`dotnet build` (projeto principal) e `dotnet test` (suíte nova, filtrada) verdes — 16/16 passed.
Sem endpoint HTTP nesta etapa (próximo passo é do `@lp-backend-dev`, conforme escopo).

## Implementação — endpoints e Test Lab (2026-08-31)

`@lp-backend-dev` (Dex). Consome só a API pública do transpilador da Lia — nenhum ajuste nele.

- **`MappingRelease` (nasce aqui):** `Models/Entities/Fiscal/MappingRelease.cs` — `MappingReleaseStatus`
  (`draft_compiled`/`test_passed`/`test_failed`, sem `approved`/`published`), `MappingReleaseArtifact`,
  `MappingReleaseCompileDiagnostic`, `MappingTestRunSummary` (`RequiredGatesPassed` — contrato Slice 7),
  `MappingTestRunDivergence` (provenance completa por divergência).
- **Store:** `SqlMappingReleaseStore` (tabela `tbMappingRelease`, mesmo padrão ADO.NET/DDL idempotente
  de `SqlMappingDraftStore`). Idempotência por `(DraftId, RulesSnapshotHash)` — hash = SHA-256 de
  `RuleId:ETag` de todas as regras `accepted`/`edited` do draft; editar uma regra já compilada muda o
  hash e gera uma release nova (correto — o snapshot mudou).
- **Compilação (`POST .../compile`):** `MappingCompileService` — 202 + `jobId` (padrão observável:
  `GET .../compile/{jobId}`), fire-and-forget via `IServiceScopeFactory`. Lê as regras do draft
  (`IMappingDraftStore.GetDraftIfMemberAsync`, isolamento por workspace já embutido), chama
  `MappingDraftRuleTranspiler.ToXslt`/`ToTcl` conforme `draft.Engine`, persiste artefato + diagnósticos
  de compilação. **Achado corrigido durante a implementação:** o nome do elemento raiz do XSLT não pode
  ser um GUID cru (`draft.PackageId.ToString()`) — GUID pode começar com dígito, inválido como NCName
  XML (`XmlException: Name cannot begin with the '0' character`); usa `root{PackageId:N}` em vez disso.
- **Fiscal Test Lab (`POST .../test-runs`):** `MappingTestRunService` — 202 + `jobId`
  (`GET .../test-runs/{jobId}`). Só executa de verdade `engine=xslt` (via `XsltApplier`, já usado pelo
  `RepairOrchestrator`) — `engine=tcl` não tem runner determinístico neste repositório (o runner
  Sysmiddle real está fora do alcance deste slice); o job conclui `completed` com
  `RequiredGatesPassed=false` e diagnóstico explícito, nunca finge sucesso.
  - **Fixture ad-hoc, não catálogo:** o design (§4) previa `{ testCaseId | suiteId }`; não existe
    `MappingTestCase` persistido neste slice (não pedido além do endpoint) — o corpo aceita
    `{ releaseId, inputXml, expectedXml, xsdVersion? }` diretamente. Decisão de escopo registrada aqui
    para o Slice 7/backlog, não uma pendência escondida.
  - **Diff canônico:** `XslSynth.Core.CanonicalDiffer` entre o XML produzido e o `expectedXml`.
    **Achado corrigido:** o atributo `lp:ruleId` que o transpilador embute para rastreabilidade (spec
    §11) poluía o diff como divergência espúria contra qualquer gabarito real (que nunca teria esse
    atributo) — `MappingTestRunService` remove `lp:ruleId` do XML produzido antes do diff
    (`StripProvenanceAttributes`), mantendo a provenance por outro caminho (ver abaixo).
  - **Validação XSD:** `XsdValidationService.ValidateXmlAgainstXsdAsync` (best-effort, mesmo princípio
    de resiliência do projeto) — quando o tipo de documento fiscal não é detectável (fixture fora de
    NFe/CTe/NFCom/MDFe, comum em teste sintético), o resultado é tratado como informacional, não
    bloqueia `RequiredGatesPassed` (só o diff canônico bloqueia nesse caso).
  - **Provenance (spec §11):** para cada `NodeDiff`, o último segmento do XPath (nome do elemento) é
    casado contra o último segmento de `TargetRefs[0]` das regras `accepted`/`edited` do draft —
    resolve `MappingDraftRuleDetail` (RuleId + SourceRefs + Evidence) sem depender do atributo
    removido do diff. Testado explicitamente (`MappingTestRunServiceTests`): divergência → `RuleId`
    correto → `SourceRefs` da regra de origem.
  - **`RequiredGatesPassed`:** `divergences.Count == 0 && xsdValid` (com a ressalva de XSD acima).
    Atualiza `MappingRelease.Status` para `test_passed`/`test_failed` — testado explicitamente nos
    dois sentidos.
- **Endpoints:** `MappingCompilationController` (`Controllers/MappingCompilationController.cs`), mesma
  rota-base `api/workspaces/{workspaceId}` e `[ServiceFilter(typeof(MappingEngineGuardFilter))]` dos
  Slices 3/4 (defesa em profundidade — o motor real já vem validado do `MappingDraft`, nunca
  `sysmiddle`). `POST .../compile`, `GET .../compile/{jobId}`, `GET .../releases/{releaseId}`,
  `POST .../test-runs`, `GET .../test-runs/{jobId}` — todos com isolamento cross-workspace fail-closed
  (mesmo padrão "não existe" == "não é seu" dos slices anteriores).
- **DI:** `Program.cs`, grupo Database (Slice 5) — `IMappingReleaseStore`/`SqlMappingReleaseStore`,
  `IMappingCompileService`/`MappingCompileService`, `IMappingTestRunService`/`MappingTestRunService`,
  todos `Scoped`. `XsdValidationService` já estava registrado (reaproveitado, não duplicado).

**Testes novos:** `MappingCompileServiceTests` (draft_compiled, idempotência por snapshot, isolamento
cross-workspace) e `MappingTestRunServiceTests` (pass com provenance vazia, fail com provenance até a
regra, isolamento cross-workspace) — 6 casos, mais os 16 já existentes do transpilador. `dotnet build`
(projeto principal) e `dotnet test` (suíte completa, 504 casos) verdes.
