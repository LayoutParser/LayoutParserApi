# Design — Reconstrução reversa best-effort XML→TXT (Issue #151, investigação Fase 4)

Autor: `@lp-parser-llm` (Lia). Investigação solicitada pelo dono — entregável é design +
plano de execução em fases, **não implementação completa**. Bloqueio original (#139/#140)
foi removido: ambas CLOSED.

## 1. O que a issue pede (releitura do critério de aceite)

Caso de uso do dono: dado um **XML SEFAZ já transformado** + o **layout de input** (TXT
original do cliente) + a **transformação selecionada** (mapper), reconstruir o TXT que o
cliente emitiu — "best-effort", explicitamente não prometido como 100% reversível.

O veredito de viabilidade já foi produzido por `@lp-architect` em
[`design-xslsynth-runtime-e-reversibilidade-2026-08-16.md`](design-xslsynth-runtime-e-reversibilidade-2026-08-16.md)
§2 (issue #151 nasceu diretamente dessa seção — ver corpo da issue). Não reabro esse
veredito; ele já é o critério de aceite formal:

- **NÃO** prometer reversão automática e genérica de qualquer `StructuredRule`.
- Cada `StructuredBranch`/função carrega metadado `Reversible: bool`.
- Campos não-reversíveis aparecem **sinalizados** no output (placeholder + flag), nunca
  valor inventado.
- Quando o TXT original existir na sessão do usuário, a reconstrução é **validada** contra
  ele (bate/não bate) — não apenas gerada às cegas.
- Reaproveita `StructuredRule`/`FunctionCatalog` existentes via `Direction` (Forward|Reverse)
  como parâmetro — **não duplica** parser DSL nem catálogo de funções.

Este documento assume esse critério como dado e foca no **como**: que peças já existem
(de #139/#140/#141), o que falta, e em que ordem construir.

## 2. Inventário do que #139-141 já entregou (é suficiente como base?)

Investigado o código real em `ai/XslSynth.Contracts/Core/StructuralResolution/` e
`ai/XslSynth.Contracts/Prompting/`:

| Peça | Existe? | Serve pra reversão como está? |
|------|---------|-------------------------------|
| `XmlLayoutStructureParser` + `XmlLayoutCatalog` (XSD NF-e → árvore de nós, XPath absoluto) | ✅ | Sim, direto — o catálogo é direção-agnóstico (é só "onde fica cada campo no XML"), usado igual nos dois sentidos. |
| `MappingKindClassifier` (direct/transformed/concatenated/static) | ✅ | Sim, mesma classificação serve para decidir a estratégia de inversão (ver §3). |
| `OccurrenceResolver` (TXT linha-ocorrência ↔ XML ocorrência) | ✅ | Parcial — resolve `IsPositionalGroupRepetition` (linha→XML). Reverso (XML ocorrência N → qual linha física do TXT) não foi escrito; é o mesmo problema, direção trocada, mas o código atual assume "sources conhecidos, resolve destino". Precisa de um método simétrico. |
| `FieldToXmlMappingComposer` (critério `authoritative`/`best-effort`, 5 condições) | ✅ | O critério em si (§5 do design #140) é reaproveitável quase 1:1 para o sentido inverso — mesmas 5 perguntas, mesma semântica de `Limitations`. Não precisa reinventar, precisa generalizar `MappingCandidate`/`FieldToXmlMapping` para não assumir TXT→XML como única direção nos comentários/nomes. |
| `StructuredRule`/`StructuredBranch` (Camada 0-1, `DslStructuredParser`) | ✅ | Sim — é a árvore de atribuição citada no design de reversibilidade. Ainda **sem** o campo `Reversible: bool` pedido no critério de aceite (gap real, ver §4.1). |
| `FunctionCatalog` (Camada 2, extraído via `MetadataLoadContext` reflection-only) | ✅ | **Insuficiente sozinho.** Hoje só tem `Name`/`FullTypeName`/`Category`/`Signature`/`NameIsReliable` — **não tem noção de reversibilidade nem de "com perda"**. E a limitação de ofuscação documentada na própria classe (`Execute(object[] pars)` sempre, corpo real só em runtime) significa que não dá pra INFERIR automaticamente se uma função é bijetora só por reflection — precisa de uma lista curada manualmente (ver §4.1). |
| Mecanismo de "TXT original na sessão" | ✅ (parcial) | `session-artifacts-sharing-design` já desenhou sessão/histórico de usuário (`AiUserSession`) — mas isso guarda regra/candidato gerado, não necessariamente o **TXT de entrada bruto** que o usuário subiu. Confirmar com `@lp-backend-dev`/`@lp-qa` se o pipeline de parse hoje persiste o TXT original em algum lugar acessível por sessão (é dado sensível — ver §5). |

**Conclusão:** a base estrutural (#139/#140) é suficiente para **construir sobre ela**, mas
não é suficiente sozinha. Faltam três peças concretas, nenhuma delas grande isoladamente,
mas juntas formam o escopo real de #151:

1. Metadado de reversibilidade em `StructuredBranch`/função (dado novo, curado).
2. `OccurrenceResolver` simétrico (XML→linha).
3. Confirmação/localização do artefato "TXT original" acessível por sessão.

## 3. Estratégia de inversão por `MappingKind` (reaproveitando o classificador existente)

`MappingKindClassifier` já separa em 4 categorias — a estratégia de reversão é diferente
para cada uma, e é isso que dá o "não invente mecanismo novo, generalize o que existe":

| `MappingKind` | Forward (existente) | Reverse (novo) |
|---|---|---|
| `Direct` (cópia 1:1, sem função) | `Sources[0]` → `Target` | `Target` (valor no XML) → `Sources[0]` (posição no TXT). Trivial, sempre `Reversible = true`. |
| `Static` (valor literal, sem `I.`) | `StaticValue` → `Target` | Nada a reconstruir no TXT (não veio de campo de origem) — não gera `TxtFieldReference`, não é erro, é fora de escopo por natureza. |
| `Transformed` (uma função aplicada) | `F.Funcao(Source)` → `Target` | Só se a função individual for `Reversible = true` (ex.: `Trim`/`PadLeft` com padding conhecido são revertíveis; `CalculateVerifierDigit` não é). Aplica a função inversa catalogada; se não houver, marca `BestEffort` com `Limitations`. |
| `Concatenated` (`ConcatString` de N sources) | `F.ConcatString(S1,S2,...)` → `Target` | Só revertível se o delimitador for conhecido E cada segmento tiver tamanho fixo/detectável (mesma lógica de campo posicional). Caso ambíguo → `BestEffort`, todos os `Sources` daquele grupo ficam com placeholder + `Limitations` explicando a ambiguidade — nunca adivinha o split. |

Isso é literalmente a mesma tabela do design de reversibilidade (§2, itens 1-3), só
amarrada aos 4 valores reais do enum `MappingKind` já implementado — não é invenção nova,
é a costura entre o veredito arquitetural e o código que já existe.

## 4. O que falta construir — plano de fases

### Fase A — Metadado de reversibilidade (pré-requisito, pequeno e isolado)

- Adicionar `bool Reversible` (e opcionalmente `string? IrreversibilityReason`) a
  `FunctionCatalogEntry` — **não** inferido por reflection (a ofuscação impede isso com
  confiança, já documentado na classe). Curado manualmente: lista fechada de nomes-palpite
  conhecidos (`ConcatString`, `Trim`, `PadLeft/Right`, `CalculateVerifierDigit`,
  `FormatDate`, etc.) com o veredito de bijetividade decidido caso a caso, revisável por
  humano — mesmo espírito de `NameIsReliable: false` (já existe o padrão "sinalize
  incerteza, não finja certeza" nesse arquivo).
- Adicionar `bool Reversible` computado em `StructuredBranch` (ou calculado sob demanda a
  partir das `Functions` referenciadas + a lista curada acima — provavelmente não precisa
  virar campo persistido, pode ser um `IsReversible(branch, catalog)` puro).
- Sem dependência de nada além do que já existe em `XslSynth.Contracts`. Pode ser feito
  isoladamente e testado com unit tests puros (sem XSD, sem I/O) — bom primeiro PR.

### Fase B — `OccurrenceResolver` simétrico + `MappingCandidate`/`Composer` com `Direction`

- Adicionar ao `OccurrenceResolver` um método reverso: dado um nó XML com N ocorrências e
  um índice de ocorrência, resolver a que ocorrência de linha física do TXT ele corresponde
  (mesmo uso de `IsPositionalGroupRepetition`, só invertendo qual lado é conhecido).
- Generalizar `MappingCandidate`/`FieldToXmlMapping`/`FieldToXmlMappingComposer` para
  aceitar `Direction: Forward | Reverse` como parâmetro do `Compose(...)`, reaproveitando
  as mesmas 5 condições de `authoritative`/`best-effort` (a lógica de "resolvido por
  heurística de nome" e "loop dinâmico não resolvível estruturalmente" vale exatamente
  igual pro sentido inverso — só troca qual lado é o "conhecido").
- Escopo médio — é principalmente generalização de tipos existentes, não código novo do
  zero. Testável com as mesmas fixtures sintéticas já usadas em #140 (25 testes existentes
  são um bom ponto de partida para espelhar casos reversos).

### Fase C — `ReverseReconstructionService` (novo, runtime da API)

- Serviço novo em `Services/Transformation/` (ou pasta nova, a definir com
  `@lp-backend-dev`), consumindo `XslSynth.Contracts` como as demais peças de #141. Fluxo:
  1. Recebe XML de entrada + `TargetLayoutGuid`/mapper selecionado + (opcional) referência
     de sessão com o TXT original.
  2. Para cada campo do XML resolvido pelo catálogo GUID→XPath, percorre a `StructuredRule`
     correspondente na direção Reverse (Fase B) e monta candidatos a `TxtFieldReference`.
  3. **Se o TXT original da sessão existir:** usa-o como fonte de verdade e roda a
     reconstrução como **validação** (bate campo a campo?), não como geração cega — é o
     mesmo padrão `CanonicalDiffer`/gerar→validar→corrigir já usado no loop RAG (ver
     memória `rag-improve`). Reporta divergências, não silencia.
  4. **Se não existir:** gera o TXT best-effort, com campos não-reversíveis marcados
     (placeholder + `Limitations`), nunca inventando valor.
- Endpoint HTTP novo (dono `@lp-backend-dev`, análogo ao padrão de #141 —
  `TransformationExecutionController`), fire-and-forget se custoso, resposta sempre com o
  TXT + lista de `Limitations`/campos não resolvidos, nunca "sucesso silencioso" quando há
  best-effort envolvido (honestidade de métrica, princípio já seguido em #139-141).

### Fase D — Validação comportamental (QA)

- `@lp-qa`: rodar a reconstrução contra os pares TXT/XML reais já existentes no corpus
  (ver memórias de `nt-pipeline-p1-p2-real-run`, `finetuning-poc-fase1-dataset` — pares
  `tcl→xsl`/gabaritos reais já catalogados) e medir taxa de campos corretos vs.
  `BestEffort` vs. divergentes. Sem essa medição real, qualquer número de "funciona X%"
  antes da Fase D é estimativa não validada — não declarar sucesso sem isso.

## 5. Riscos e decisões em aberto para o dono

1. **Escopo não-trivial.** Fases A-D não são um "ajuste pequeno" — B e C tocam tipos
   compartilhados (`MappingCandidate`, `FieldToXmlMappingComposer`) usados hoje só em
   Forward; qualquer mudança ali tem risco de regressão no caminho já em produção de #141
   (`/fieldMappings`). Recomendo Fase B ser feita com os 25 testes existentes de #140 como
   guard-rail obrigatório antes de qualquer merge.
2. **Onde o TXT original fica persistido por sessão ainda não está confirmado neste
   documento** — é uma pergunta concreta para `@lp-backend-dev`/`@lp-architect` antes da
   Fase C: se não existe hoje, a Fase C vira só "best-effort sem validação" até que
   `AiUserSession` (já desenhado, ver `session-artifacts-sharing-design`) seja estendido
   para guardar o artefato bruto — o que é dado potencialmente sensível (documento fiscal
   do cliente), então precisa de decisão explícita de retenção/expurgo, não default.
3. **A lista curada de funções reversíveis (Fase A) é trabalho manual, não automatizável**
   — a ofuscação da DLL Sysmiddle impede inferência confiável (mesma limitação já
   documentada em `FunctionCatalog`). Alguém (Lia, com revisão do dono) precisa revisar
   caso a caso as funções mais usadas no corpus real antes de a Fase A ser útil — não é
   "escrever código", é "decidir semântica de negócio por função".
4. **Nenhum dado real de cliente deve alimentar o design/testes desta fase sem autorização**
   — os pares TXT/XML do corpus já catalogado (nt-pipeline, fine-tuning POC) são os únicos
   aprovados para uso; não usar documentos novos sem confirmar procedência.

## 6. Recomendação de sequenciamento

Fases A e B são pequenas, isoladas, testáveis sem infra nova — **candidatas a um primeiro
PR de validação de viabilidade** antes de comprometer a Fase C/D (que são as que têm custo
real: endpoint novo, decisão de retenção de dado sensível, medição contra corpus). Sugiro
ao dono aprovar A+B como "spike pago" e decidir C/D só depois de ver o resultado real da
Fase A (quantas funções do corpus real são de fato revertíveis — pode ser que a resposta
seja "poucas", o que muda o valor de negócio de C/D).

## Arquivos consultados (não alterados)

- `docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md`
- `docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md`
- `ai/XslSynth.Contracts/Core/StructuralResolution/*.cs`
- `ai/XslSynth.Contracts/Prompting/StructuredRuleSchema.cs`, `FunctionCatalog.cs`
- `.claude/agent-memory/lp-architect/session-artifacts-sharing-design.md`
- `.claude/agent-memory/lp-parser-llm/issue-140-motor-resolucao-estrutural-implementado.md`
