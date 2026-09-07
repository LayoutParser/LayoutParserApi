# Design — Reconstrução reversa best-effort XML→TXT (Issue #151, investigação Fase 4)

Autor: `@lp-parser-llm` (Lia). Investigação solicitada pelo dono — entregável é design +
plano de execução em fases, **não implementação completa**. Bloqueio original (#139/#140)
foi removido: ambas CLOSED.

> **Nota de reconciliação (2026-09-07, `@lp-architect`):** este arquivo existia até agora só
> em `hotfix/build-quebrado-aiusersessionstore-testes-develop` (nunca chegou a `develop`/
> `master`/à branch do spike) — restaurado aqui para servir de base real ao §7 abaixo, que
> avalia o reenquadramento do dono entre "reversão pós-hoc" (o que as Fases A-D descrevem) e
> "co-geração no momento da autoria". Conteúdo das seções 1-6 preservado sem alteração;
> §7 é o acréscimo desta sessão.

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

---

## 7. Reenquadramento do dono (2026-09-07): reversão pós-hoc vs. co-geração na autoria

O dono trouxe uma pergunta anterior a "como terminar as Fases C/D": **em que ponto do
pipeline a lógica reversa deveria nascer?** As Fases A-D acima (§4) — e o spike A+B já
executado (`spike-resultado-reconstrucao-reversa-2026-09-07.md`) — assumem a **Opção 1**:
reconstruir a direção inversa por engenharia estrutural de um mapeamento já existente,
depois do fato. A **Opção 2** propõe inverter a ordem: a IA que hoje gera o mapeamento
Forward (TXT→XML) seria instruída a produzir, **no mesmo ato de geração**, os artefatos do
sentido Reverse (XML→TXT) — enquanto ainda tem acesso ao TXT original e a todo o contexto
de decisão que se perde depois.

Este documento e o spike falavam de dois subsistemas diferentes por engano de escopo —
importante desfazer essa confusão antes de comparar as opções:

- **`XslSynth.Contracts`** (medido no spike A/B, 173 funções Sysmiddle, 13,3% reversíveis) é
  o motor da engine `sysmiddle` — mapeamento runtime, não o que gera XSLT/TCL publicável.
- **`MappingDraftRuleTranspiler`** (`Services/Fiscal/MappingDraftRuleTranspiler.cs`, Slice 5,
  issue #231) é o que **realmente** compila XSLT 1.0 e TCL `<MAP><LINE><FIELD>` a partir de
  regras estruturadas (`MappingDraftRule`) — o subsistema citado no refinamento de
  `spike-resultado-...md` (commit `f102b21`) com só 5 operações: `copy`, `concat`, `lookup`,
  `conditional`, `constant` (`SupportedOperations`, linha 44-47 do arquivo). É **este** o
  alvo relevante da Opção 2, porque é o único ponto do pipeline onde "a IA gera e publica
  transformação" hoje existe de fato em produção (Slice 3→5, ver `MappingSuggestionService`
  + `TclExplanationAdapter`/`XsltExplanationAdapter`).

### 7.1 O que já existe hoje no pipeline de autoria (lido no código, não suposto)

`MappingDraftRule` **já é estruturado antes de virar TCL/XSLT** — não é texto opaco desde o
início. O fluxo real (`Services/Fiscal/MappingSuggestionService.cs` + `MappingDraftRuleTranspiler.cs`):

1. `MappingSuggestionService.EnqueueAsync` chama o Ollama com os artefatos do draft (spec/
   XSD/sample) e recebe de volta uma lista de propostas com `SourceRefs`/`TargetRefs`/
   `Operation`/`ConditionsJson`/`TransformationsJson` — **já no formato de regra**, não texto
   TCL/XSLT gerado por prompt livre.
2. Humano revisa e muda o `Status` de cada `MappingDraftRule` para `Accepted`/`Edited` (ou
   `Rejected`/`NeedsInput`).
3. `MappingDraftRuleTranspiler.ToXslt`/`ToTcl` — **determinístico, sem IA** — compila as
   regras aceitas para o texto final, um `switch` por `Operation` (`BuildCopy`/`BuildConcat`/
   `BuildLookup`/`BuildConditional`/`BuildConstant`).

Isso muda o cálculo da Opção 2 de forma importante: a "co-geração" não precisaria acontecer
no passo 3 (compilação, hoje determinística e sem IA — não faz sentido pedir pra um `switch`
"também gerar o reverso", ele já não decide nada) nem exigiria uma segunda chamada de IA
dedicada à direção reversa. O ponto de alavancagem real é o **passo 1**: quando o Ollama
propõe `{SourceRefs, TargetRefs, Operation, ...}` para o sentido Forward, o mesmo raciocínio
que produziu essa regra já contém (ou podia conter, se o prompt pedir) a resposta para "essa
operação é invertível, e se for, qual é a regra inversa" — porque o modelo está olhando
TXT+XML+XSD ao mesmo tempo nesse momento, coisa que o passo 3 (determinístico) nunca vê.

### 7.2 Custo/complexidade real de cada opção

**Opção 1 (reversão pós-hoc, o que o spike A+B mediu):**

- Código parcial já existe e está testado: `FunctionReversibilityCatalog` (175 entradas
  curadas), `BranchReversibilityResolver`, `Direction` no `FieldToXmlMappingComposer` — mas
  tudo isso vive em `XslSynth.Contracts`, o subsistema **errado** para o caso de uso real de
  #151 tal como o dono descreveu (mapeamento TCL/XSLT publicado via `MappingDraftRuleTranspiler`).
  Não há hoje nenhum catálogo de reversibilidade equivalente para as 5 operações de
  `MappingDraftRuleTranspiler` — isso teria que ser construído do zero, mas é um catálogo
  **muito menor** (5 operações vs. 173 funções): `copy` trivialmente reversível, `constant`
  trivialmente **não**-reversível (não carrega origem nenhuma), `concat`/`lookup`/
  `conditional` reversíveis só sob condição (delimitador conhecido e sem colisão para
  `concat`; tabela injetora para `lookup`; ramos com saída discriminável para `conditional`)
  — dá o teto de 20-40% citado no refinamento do spike, agora confirmado como o catálogo
  certo a curar (não o de 173 funções Sysmiddle).
- Além do catálogo de reversibilidade por operação, a Opção 1 pura (sem TXT original)
  precisa **também** resolver, por engenharia reversa, qual TXT teria produzido aquele XML —
  ou seja, precisa reimplementar em sentido contrário a lógica de posicionamento física de
  campo (`OccurrenceResolver` simétrico, §4 Fase B) só a partir da regra estruturada, sem
  nunca ter visto o TXT de origem. Esse é o trabalho mais caro e o que mais risco de
  regressão carrega (toca `MappingCandidate`/`Composer`, hoje só usados em Forward).

**Opção 2 (co-geração na autoria):**

- Não elimina a necessidade de um catálogo de reversibilidade por operação — a IA ainda
  precisa saber (ou o prompt ainda precisa instruir) que `constant` nunca é reversível e que
  `concat`/`lookup`/`conditional` só são sob condição. Esse catálogo de 5 operações é
  **compartilhado** pelas duas opções — não é custo exclusivo de nenhuma.
- O que a Opção 2 **realmente** evita é o problema mais caro da Opção 1: resolver a posição
  física do TXT a partir do zero, sem nunca ter visto o TXT. No momento da autoria, o TXT
  original **é** um dos artefatos de entrada do draft (linha 1 do fluxo em §7.1 — a
  `MappingSuggestionService` já lê os artefatos do draft, que inclui a amostra de origem).
  A IA gerando a regra reversa nesse momento não está "adivinhando" a posição física — está
  **copiando** uma informação que ela já tem na mesma janela de contexto que gerou a regra
  Forward. Isso é estruturalmente mais barato que o `OccurrenceResolver` simétrico da Fase B,
  porque não exige reconstruir por lógica o que já está disponível como dado.
- Custo novo que a Opção 2 introduz e a Opção 1 não tem: **exige mudar o contrato de
  `MappingDraftRule`** para carregar uma segunda regra (ou um par `{Forward, Reverse}`) — ver
  §7.4 — e potencialmente uma segunda passada de prompt/parsing de resposta do Ollama (não
  necessariamente uma segunda *chamada* de rede, ver §7.4).

**Veredito de custo:** a Opção 2 não é mais barata em curadoria de reversibilidade por
operação (esse custo é compartilhado), mas é **substancialmente mais barata** na parte mais
cara e arriscada da Opção 1 — resolver posição física do TXT sem o TXT. Em troca, exige
mudança de contrato/pipeline de geração (Slice 3/5) que a Opção 1 não exige.

### 7.3 A Opção 2 contorna funções não-bijetoras, ou só desloca onde o problema aparece?

**Desloca, não contorna — e é importante ser honesto sobre isso.** `constant` é lossy por
definição matemática (o valor de destino não carrega nenhuma informação sobre uma origem,
porque não existe origem): nem a IA gerando os dois sentidos ao mesmo tempo pode inventar de
onde um campo `constant` "veio" no TXT, porque ele não veio de lugar nenhum — foi escrito
fixo pela regra. O mesmo vale, por extensão do domínio real (`sysmiddle`, fora do escopo de
`MappingDraftRuleTranspiler`, mas citado no critério de aceite da issue), para
`CalculateVerifierDigit`: um dígito verificador é uma função de hash de baixa dimensão sobre
os dígitos anteriores — não há informação suficiente no dígito resultante para reconstruir
univocamente a entrada, **mesmo sabendo a fórmula**, porque múltiplas entradas podem colidir
no mesmo dígito. Gerar os dois sentidos "ao mesmo tempo" não resolve uma perda de informação
que é da matemática da função, não da falta de contexto do gerador.

O que a Opção 2 genuinamente resolve é diferente: **não é a irreversibilidade da função, é a
perda de contexto entre geração e reconstrução**. Hoje (Opção 1), se o TXT original não
está mais disponível quando alguém precisa reconstruí-lo, a única saída é inferir a partir da
regra — e é aí que entram os problemas de posição física/delimitador ambíguo (não
necessariamente de função lossy). A Opção 2 resolve **essa** classe de problema retendo,
explicitamente, o dado que seria perdido — na prática, ou (a) persistindo o TXT/campo de
origem junto ao draft aceito (dado sensível, mesma discussão já registrada em §5 item 2), ou
(b) fazendo a IA registrar explicitamente, por campo `constant`/lossy, qual foi o valor de
origem observado na amostra usada para gerar a regra (efetivamente uma cópia parcial do TXT,
disfarçada de metadado da regra).

Ou seja: a Opção 2 não é "reversão sem perda" — é "retenção deliberada do dado que seria
perdido, decidida no momento em que ele ainda existe", em vez de "tentar inferir depois um
dado que já não existe mais". É uma mudança real de trade-off (vale a pena), não uma
mágica que elimina a irreversibilidade matemática — e deve ser comunicada ao dono nesses
termos, para não prometer "reversibilidade 100%" por engano de enquadramento.

### 7.4 Impacto na arquitetura existente (Slice 5, `MappingDraftRuleTranspiler`)

Lendo o código real (`Services/Fiscal/MappingDraftRuleTranspiler.cs`,
`Services/Fiscal/MappingSuggestionService.cs`):

- **Contrato de `MappingDraftRule`:** precisaria de um campo novo (ex.:
  `ReverseTransformationsJson`/`Direction`-aware ou uma segunda entidade
  `MappingDraftRuleReverse` 1:1 com a regra Forward) — mudança de schema em
  `Models/Entities/Fiscal/MappingDraftRule.cs` e no store SQL (`IMappingDraftStore`), com
  migração. Não é trivial, mas é aditivo (não quebra o que já existe em Forward).
- **Armazenamento:** dobra o que fica persistido por `MappingDraftRule` aceita — cada regra
  ganha uma contraparte reversa (ou um "não aplicável", para `constant`/`lookup` com tabela
  não-injetora). Para os campos lossy tratados pela retenção explícita (§7.3), o custo de
  armazenamento é maior ainda — está guardando parte do dado de origem, não só a regra.
  Volume real (quantos mappings/campos existem hoje) não foi medido nesta sessão — é uma
  pergunta concreta para `@lp-backend-dev` antes de comprometer a fase.
- **Custo de geração:** não necessariamente dobra o número de *chamadas* de rede ao Ollama —
  o prompt de `MappingSuggestionService.EnqueueAsync` já lê os artefatos completos do draft
  (spec/XSD/sample) por regra proposta; pedir que a mesma resposta inclua a contraparte
  reversa (quando aplicável) é mais token de saída por chamada, não necessariamente uma
  segunda chamada. Dito isso, o parsing de resposta (`SourceRefs`/`TargetRefs`/... hoje,
  visto em `MappingSuggestionService.cs` linha ~278-304) precisa ganhar os campos reversos —
  mudança de contrato de resposta do LLM, com o risco usual de resposta malformada/parcial
  que esse tipo de mudança carrega (mitigável com o mesmo padrão de degradação graciosa já
  usado no serviço: se a IA não conseguir propor o reverso, cai pra "não reversível", nunca
  quebra a proposta Forward).
- **`MappingDraftRuleTranspiler` (compilação):** ganha um segundo par de métodos
  (`ToXsltReverse`/`ToTclReverse`, análogos aos atuais) — reaproveita quase toda a
  infraestrutura de emissão (`BuildXPathStringLiteral`, `Escape`, provenance `lp:ruleId`),
  já que o formato de saída (XSLT/TCL) é o mesmo, só a direção do `SourceRefs`↔`TargetRefs`
  muda. Baixo risco de regressão no caminho Forward existente, porque seria um método novo,
  não uma modificação dos existentes.
- **Adapters de explicação** (`TclExplanationAdapter`/`XsltExplanationAdapter`): precisariam
  de um segundo modo de leitura (`ExplainAsync` já tem branch por `Version`/`Engine` — cabe
  um branch novo por direção), mas isso é extensão aditiva do padrão já existente, não
  redesenho.

**Resumo honesto:** a Opção 2 é uma mudança de contrato real (schema + prompt + parsing +
2 métodos novos de transpilação + extensão de adapter), não um ajuste cosmético — mas é
**aditiva** em todos os pontos tocados, o que reduz risco de regressão comparado à Opção 1
(que precisa generalizar tipos já em produção — `MappingCandidate`/`Composer` — usados hoje
só em Forward, conforme já sinalizado em §5 item 1).

### 7.5 Um caminho híbrido é plausível?

Sim, e é o caminho recomendado (§7.6). As duas opções não são mutuamente exclusivas — elas
respondem a situações diferentes:

- **Mappings novos, gerados a partir de agora:** Opção 2 — co-gerar o reverso no momento da
  autoria, quando o TXT original e o contexto completo ainda existem. Mais barato e mais
  honesto (retém dado real em vez de inferir).
- **Mappings já existentes hoje (todo o corpus atual de `MappingDraftRule` aceitas antes da
  mudança):** não têm — e não podem retroativamente ganhar — a contraparte reversa
  co-gerada, porque o momento de geração já passou. Para esses, o único caminho é a Opção 1
  (reversão estrutural pós-hoc, best-effort, exatamente como as Fases A-D/§3 já descrevem),
  usando o catálogo de reversibilidade das 5 operações (§7.2) como teto de expectativa
  (20-40%).
- **Fallback em runtime:** o serviço de reconstrução (Fase C, §4) checa primeiro se existe
  contraparte Reverse co-gerada para a regra em questão; se não existir (mapping antigo, ou
  operação para a qual a IA não conseguiu propor reverso), cai para a tentativa estrutural
  best-effort da Opção 1. Isso é literalmente o mesmo padrão de degradação graciosa já
  praticado no projeto (dotnet-standards.md §Resiliência) aplicado a uma fonte de dado em vez
  de uma dependência externa.

O catálogo de reversibilidade por operação (5 entradas: `copy`/`concat`/`lookup`/
`conditional`/`constant`) é o único artefato que **as duas opções precisam de qualquer
forma** — deveria ser construído uma vez, independente de qual opção for priorizada primeiro,
porque ambas o consomem (Opção 1 pra decidir se tenta reverter; Opção 2 pra decidir se pede
à IA pra co-gerar ou já marca "sem reverso possível").

### 7.6 Recomendação final

**Recomendo o caminho híbrido, começando pelo catálogo de reversibilidade das 5 operações
(pré-requisito comum), seguido de Opção 2 para mappings novos, com Opção 1/best-effort como
fallback para o corpus já existente.** Não recomendo comprometer a Opção 1 completa (Fases
C/D do §4, como descritas — que assumem reversão 100% pós-hoc) como único caminho, porque:

1. O trabalho mais caro e arriscado da Opção 1 (resolver posição física do TXT sem o TXT) é
   evitável quando o TXT existe no momento da geração — o que é o caso comum, já que
   `MappingSuggestionService` já lê a amostra de origem hoje.
2. A Opção 2 é aditiva na arquitetura existente (baixo risco de regressão), enquanto a Opção
   1/Fase B mexe em tipos já em produção usados só em Forward (risco de regressão real, já
   sinalizado em §5 item 1 desde a versão original deste documento).
3. Nenhuma das duas opções, sozinha, resolve mappings já existentes hoje — então a Opção 1
   best-effort continua necessária como fallback, não é descartável.

**Estimativa de esforço** (baseada no código real lido nesta sessão — `MappingDraftRuleTranspiler.cs`
tem ~480 linhas, `MappingSuggestionService.cs` já existe e tem toda a infra de chamada Ollama
+ parsing; não é estimativa às cegas, mas segue sendo estimativa, não medição):

| Etapa | Escopo | Esforço relativo |
|---|---|---|
| 0. Catálogo de reversibilidade das 5 operações de `MappingDraftRuleTranspiler` (pré-requisito comum) | Curadoria manual + testes unitários puros, análogo em espírito à `FunctionReversibilityCatalog` já feita no spike A/B, mas ~35x menor (5 operações vs. 173 funções) | Pequeno — provável 1 PR, ordem de grandeza do que já foi a Fase A do spike |
| 1. Opção 2 — schema (`MappingDraftRule` + store) | Campo/entidade nova + migração SQL, aditivo | Pequeno-médio |
| 2. Opção 2 — prompt/parsing (`MappingSuggestionService`) | Estender prompt + DTO de resposta + degradação graciosa quando IA não propõe reverso | Médio — é o ponto de maior incerteza real (qualidade da resposta do Ollama local é desconhecida até testar) |
| 3. Opção 2 — transpilação (`MappingDraftRuleTranspiler`) | 2 métodos novos (`ToXsltReverse`/`ToTclReverse`), reaproveitando helpers existentes | Pequeno — mecânico, baixo risco |
| 4. Opção 2 — adapters de explicação | Branch novo por direção nos 2 adapters existentes | Pequeno |
| 5. Opção 1 — fallback best-effort para corpus existente | Fases A-D do §4 originais, escopo já mapeado (Fase B é a parte cara: `OccurrenceResolver` simétrico + generalização de `Composer`) | Médio-grande — mantém a estimativa qualitativa já registrada em §5/§6 (não há número novo a acrescentar aqui) |
| 6. QA/medição real (ambas) | Corpus real de pares TXT/XML, bloqueado hoje por falta de acesso a dado publicado (mesma limitação já registrada no spike A/B §3) | Depende de destravar acesso ao corpus — pré-requisito de dado, não de código |

Etapas 0-4 (catálogo + Opção 2 completa) formam um primeiro incremento coerente e menor que
recomprometer as Fases C/D inteiras da Opção 1. Etapa 5 (fallback) só deveria ser priorizada
depois de ver o comportamento real da etapa 2 (a IA local consegue propor reverso de
qualidade suficiente? — pergunta em aberto, não assumir que sim).
