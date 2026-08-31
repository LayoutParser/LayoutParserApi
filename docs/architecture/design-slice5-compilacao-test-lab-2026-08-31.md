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
