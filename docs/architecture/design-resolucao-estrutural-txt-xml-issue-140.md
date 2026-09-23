# Design: resolução estrutural TXT↔XML — issue #140

**Não implementado** — desenho de arquitetura (`@lp-architect`). Pré-requisito #139 (parser
`MapperVo` canônico) tem PR #201 verde, ainda não mergeado; este design assume `RealMapperParser`/
`XslSynth.Model.MapperVo` (Parser B) como fonte única de leitura de mapeador, conforme decidido em
`docs/architecture/inventario-parsers-mapperVo-issue-139.md`.

## 0. Escopo e não-escopo

Objetivo: gerar `FieldToXmlMapping[]` — resolução **estrutural** entre campos posicionais
(`I.<Linha>/<Campo>`) e nós do XML NF-e de destino, sem executar o `LowCodeRunner` e sem comparar
valores. Não-escopo: qualquer coisa que dependa de amostra real de produção fora do Git (herdado
de #139 §5) — este design assume que a confirmação campo-a-campo do `MapperVO` real e a validação
comportamental (§6) rodam com fixtures sintéticas primeiro, e só avançam para produção sob
supervisão do dono.

## 1. Onde cada peça já existe (reaproveitar, não recriar)

| Peça do modelo pedido | Fonte real no código hoje |
|---|---|
| `TxtFieldReference.lineOccurrence` | `Models/Entities/ParsedField.cs` — `Occurrence` (índice físico 1-based) + `OccurrenceCount` (total do grupo) + `IsAggregatedOccurrence`. **Já existe e é a ocorrência física real do parse**, não um índice arbitrário — é o campo certo para popular `lineOccurrence`. |
| `TxtFieldReference.lineGuid/fieldGuid` | `Models/Entities/LineElement.ElementGuid` / `FieldElement.ElementGuid` (layout de origem, já carregado por `XmlLayoutLoader`). |
| Origem `I.<Linha>/<Campo>` na DSL | `StructuredRule.AllSources`/`StructuredBranch.Sources` (`ai/XslSynth.Contracts/Prompting/StructuredRuleSchema.cs`) — já é uma lista de strings `"Linha/Campo"`, produzida deterministicamente por `DslStructuredParser` via `MappingStructureService.ParseRule`. Não precisa de regex novo — é reaproveitamento direto. |
| `mappingKind` (direct/transformed/concatenated/static) | Ver §3 — derivável de `StructuredRule` sem parser novo. |
| `XmlNodeReference.xpath` | **Não existe hoje um catálogo `TargetElementGuid → XPath`.** É o gap central deste design — ver §2. |
| `xmlOccurrence` | Não existe hoje. Ver §4. |

## 2. Catálogo `TargetElementGuid → XPath`

### 2.1 De onde vem a estrutura XML de destino

`Mapper.TargetLayoutGuid` referencia um `Layout` (mesma tabela/entidade dos layouts de origem,
`LayoutDatabaseService`/`CachedLayoutService`) cujo `LayoutType` **não** é `"TextPositional"` — é
o layout XML de saída (NF-e). Confirmado por código: `LayoutType` é lido do mesmo XML raiz
(`GetNodeValue(layoutNode, "LayoutType")`, `XmlLayoutLoader.cs:39`) para qualquer tipo de layout,
não só posicional; e `Services/Generation/TxtGenerator/Models/FileLayout.cs:9` já documenta os
valores possíveis do campo como `"TextPositional, Xml, IDOC"` — ou seja, a infraestrutura de
carregar `Layout` por `LayoutGuid` já é agnóstica ao tipo, só o **parser dos `Elements`** é
hoje específico de posicional (`LineElement`/`FieldElement`).

**Proposta:** um `XmlLayoutStructureParser` novo (paralelo a `XmlLayoutLoader`, não substituto)
que lê o `Layout` cujo `LayoutGuid == TargetLayoutGuid`, detecta `LayoutType == "Xml"` e produz
uma árvore de nós (`XmlLayoutNode { ElementGuid, Kind (element|attribute|text), Name, Namespace,
ParentGuid, Children[], MinOccurs, MaxOccurs }`) a partir da estrutura XML de definição do layout
(não do XSD SEFAZ — o layout Sysmiddle já é a fonte de verdade interna, o XSD entra só na
validação, não na resolução — não confundir com o pathway de diagnóstico Ollama/XSD, que é
tema separado). Se essa estrutura Sysmiddle de destino não existir de fato hoje (precisa
confirmar contra amostra real — mesma restrição de #139 §5), o fallback é usar o XSD SEFAZ
(`nfephp-org/sped-nfe`, já resolvido como fonte confiável em memória de `@lp-architect`,
`sefaz-xsd-schema-source.md`) só para os GUIDs `TAG_/ATT_/GRT_` que não tiverem correspondência —
mas isso é fallback, não a fonte primária.

### 2.2 Construção do XPath absoluto

Percorrer a árvore de `XmlLayoutNode` da raiz até o `ElementGuid` alvo, concatenando
`Name`/`Namespace` de cada ancestral:

```
xpath = "/" + string.Join("/", ancestorChain.Select(n =>
    n.Namespace is null ? n.Name : $"{Prefix(n.Namespace)}:{n.Name}"))
```

Para `Kind == attribute`, o último segmento vira `@Name` em vez de `/Name`. Para `Kind == text`,
o XPath aponta ao elemento pai e o `nodeKind` no `XmlNodeReference` já sinaliza "texto" — não
precisa de `/text()` no XPath em si (mais estável a mudanças de whitespace/CDATA).

Namespace: a NF-e usa um único namespace default (`http://www.portalfiscal.inf.br/nfe`) — não há
múltiplos prefixos a resolver na prática, mas o modelo carrega `Namespace` por nó para não
assumir isso permanentemente (se `TargetLayoutGuid` apontar a outro domínio XML no futuro).

### 2.3 Cache

`TargetLayoutGuid → XmlLayoutNode[]` é essencialmente estático por versão de layout — mesmo
padrão de cache já usado para `Mapper`/`Layout` (`MapperCacheService`/`CachedLayoutService`,
Redis opcional + fallback sem cache). Reaproveitar a mesma infraestrutura, não criar uma nova.

## 3. `mappingKind` — derivação estrutural, sem regex ad-hoc

Fonte: `StructuredRule` (já produzido por `MappingStructureService.ParseRule`, que já não usa
regex solto — usa `DslStructuredParser`, camada 0/1 determinística). Critério, em ordem:

1. **`static`** — `StructuredRule.StaticValue != null` E `AllSources.Count == 0`. Já é exatamente
   o campo que `StructuredRuleSchema` expõe (`StaticValue: "regra sem nenhuma origem I., só
   literal"`) — não precisa de heurística nova.
2. **`direct`** — vindo de `LinkMappingItem` (não de `MapperRule`/`StructuredRule`) — mapeamento
   1:1 campo→campo sem DSL, já modelado como tipo distinto em `XslSynth.Model.MapperVo.LinkMappings`.
   Critério objetivo: item veio de `LinkMappings`, não de `Rules`.
3. **`concatenated`** — `AllSources.Count > 1` E `Functions` contém uma função de concatenação
   conhecida (`"ConcatString"` já aparece como exemplo real em `StructuredRuleSchema.cs:55`) —
   **não** inferir por contagem de sources sozinha, porque uma regra condicional pode ter 2
   sources em ramos diferentes sem concatenar nada (ver item 4).
4. **`transformed`** — qualquer `MapperRule`/`StructuredRule` que não caia em 1-3: tem função(ões)
   não-concatenadoras (`CalculateVerifierDigit` etc.), múltiplos `Branches` (condicional), ou
   `LoopType != null`. É o "catch-all" correto porque `StructuredRule` já normaliza toda a DSL —
   não há uma 5ª categoria escondida que precise de parser adicional.

Isso cobre a exigência "não depender apenas de regex" porque a classificação inteira acontece
sobre `StructuredRule` (já parseado por `DslStructuredParser`), nunca sobre `ContentValue` bruto.

### 3.1 N origens → 1 destino / 1 origem → N destinos

- **N→1**: natural em `concatenated`/`transformed` — `sources: TxtFieldReference[]` já é lista.
- **1→N**: uma mesma `TxtFieldReference` pode aparecer em múltiplos `FieldToXmlMapping` distintos
  (mapeamentos diferentes, `targets` diferentes) — não é uma estrutura nova, é resultado natural
  de iterar `Rules`/`LinkMappings` e cada um produzir seu próprio `FieldToXmlMapping`. Não precisa
  de agrupamento adicional no modelo — a pergunta "quais destinos usam este campo de origem" é uma
  *query* sobre a lista resultante (`Where(m => m.Sources.Any(s => s.FieldGuid == x))`), não um
  campo a mais no modelo.

## 4. Ocorrência física: `lineOccurrence` → `xmlOccurrence`

### 4.1 O que já existe do lado TXT

`ParsedField.Occurrence` (1-based, físico) + `OccurrenceCount` (total do grupo) já resolvem
`lineOccurrence` sem trabalho novo — ver `docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md`
e o diagnóstico do bug `OccurrenceCount`/`IsAggregatedOccurrence` (2026-08-21). Usar
`IsAggregatedOccurrence == false` (fragmento físico bruto) como fonte de `lineOccurrence` — nunca
o agregado (`Occurrence == 0`), porque a resolução estrutural desta issue precisa mapear
ocorrência física real do TXT, não o valor lógico consolidado.

### 4.2 Do lado XML — não existe hoje, é o segundo gap real

`LineElement.IsPositionalGroupRepetition`/`MinimalOccurrence`/`MaximumOccurrence` descrevem
repetição do **lado posicional**. Do lado XML, a repetição correspondente (ex.: `det[1]`,
`det[2]` na NF-e) é modelada por `XmlLayoutNode.MaxOccurs > 1` (proposto em §2.1) num nó ancestral
do `ElementGuid` alvo.

**Regra de resolução:** `xmlOccurrence` de um `XmlNodeReference` é herdado do `lineOccurrence` do
`TxtFieldReference` correspondente **quando** o `LineElement` de origem tem
`IsPositionalGroupRepetition == true` **e** existe um ancestral do `ElementGuid` de destino com
`MaxOccurs > 1` — assumindo correspondência 1:1 posicional entre a N-ésima ocorrência da linha
repetida e o N-ésimo nó XML repetido (mesma ordem de emissão, sem reordenação). Essa suposição
**é exatamente o que a validação comportamental do §6 precisa confirmar/refutar por caso real** —
não é uma garantia estrutural, é uma hipótese com evidência de teste.

Se a origem não é repetida mas o destino é (ou vice-versa) — caso `IsPositionalGroupRepetition`
não bate com `MaxOccurs > 1` no lado oposto — a resolução não pode assumir correspondência 1:1;
marcar esse `FieldToXmlMapping` como `best-effort` automaticamente (ver critério objetivo §5).

## 5. Critério objetivo `authoritative` vs. `best-effort`

Não pode ser subjetivo — regra de decisão puramente estrutural, avaliada por mapeamento:

```
authoritative quando TODAS as condições abaixo são verdadeiras:
  1. mappingKind == "static", OU
     todo TxtFieldReference do mapeamento resolveu um ElementGuid real no layout de origem
     (sem fallback por nome/heurística)
  2. todo XmlNodeReference resolveu um ElementGuid real no catálogo TargetLayoutGuid→XPath
     (§2), sem fallback de XSD SEFAZ (fallback usado ⇒ best-effort, nunca authoritative)
  3. se algum LineElement de origem envolvido tem IsPositionalGroupRepetition == true,
     o ancestral XML correspondente tem MaxOccurs > 1 (correspondência confirmada, não
     assumida por default)
  4. mappingKind != "transformed" com LoopType != null ("for"/"foreach"/"while") — loop
     dinâmico não tem contagem de ocorrência resolvível estruturalmente sem executar a DSL
  5. nenhuma Function referenciada é desconhecida no FunctionCatalog (Camada 2,
     MappingStructureService.TryExtractFunctionCatalog) — função não catalogada = destino
     pode divergir de forma não estrutural

best-effort em qualquer outro caso — incluindo divergência confirmada na validação
comportamental (§6) que não seja eliminável por ajuste do algoritmo.
```

Este critério é binário e auditável por código (cada condição é uma checagem booleana sobre
dados já disponíveis) — não depende de julgamento humano por mapeamento individual.

## 6. Plano de validação comportamental (design apenas — não implementar agora)

### 6.1 Fixtures sintéticas necessárias (sem dado real de cliente)

20 execuções controladas cobrindo a matriz pedida pelo dono. Cada fixture é um par
`(layout TXT sintético mínimo, layout XML de destino sintético mínimo, mapper sintético)`,
gerado à mão ou por um gerador determinístico simples — nunca por amostra real (mesma restrição
de #139 §5). Cobertura mínima (uma fixture por linha, algumas combinam 2 dimensões):

| # | Dimensão | Fixture |
|---|---|---|
| 1-3 | Tipo de layout de origem | TXT plano, MQSeries, IDOC — cada um com 1 campo→1 elemento direto |
| 4 | Linha repetida | `LineElement.IsPositionalGroupRepetition=true`, 3 ocorrências físicas → 3 nós XML `MaxOccurs=3` |
| 5 | Grupo repetido aninhado | Linha repetida dentro de outro grupo (2 níveis) |
| 6 | Atributo | Destino é `XmlNodeReference.nodeKind == "attribute"` |
| 7 | Concatenação | `mappingKind == "concatenated"`, 2+ sources, função `ConcatString` |
| 8 | Valor estático | `mappingKind == "static"`, sem sources |
| 9 | Campo vazio (origem) | Origem `IsDeclaredEmpty == true` (contrato de 2026-08-27) — verificar que o mapeamento não quebra, resultado é `best-effort` ou valor vazio explícito, nunca exceção |
| 10 | Condicional simples | `StructuredRule` com 2 `Branches`, sem loop |
| 11 | Função de transformação não-concat | `CalculateVerifierDigit` ou similar — `mappingKind == "transformed"` |
| 12 | Loop dinâmico | `LoopType != null` — deve cair em `best-effort` por critério §5.4 |
| 13 | N origens → 1 destino | Confirma §3.1 |
| 14 | 1 origem → N destinos | Confirma §3.1 |
| 15 | Namespace não-default | Elemento XML com prefixo diferente do NF-e padrão |
| 16 | Mismatch de repetição (origem repetida, destino não) | Confirma fallback para `best-effort` (§4.2) |
| 17 | Mismatch de repetição (destino repetido, origem não) | Idem, direção oposta |
| 18 | Função desconhecida no catálogo | `FunctionCatalog` não resolve a função → `best-effort` (§5.5) |
| 19 | `Elements` aninhados no MapperVO | Cobre a limitação confirmada em #139 §7.1 (parser B não captura aninhamento) — espera-se falha conhecida, documentar como limitação, não bug |
| 20 | Degradação posicional (`PositionalAlignmentFailed=true`) | Origem já sinaliza falha de alinhamento (contrato 2026-08-27) — mapeamento correspondente deve virar `best-effort` automaticamente, nunca `authoritative` |

### 6.2 Como comparar contra o `LowCodeRunner` sem dado real

Para cada fixture: (a) rodar a resolução estrutural proposta aqui sobre o mapper/layout
sintéticos → produz `FieldToXmlMapping[]` previsto; (b) rodar o `LowCodeRunner` real sobre o
mesmo par TXT/mapper sintético (documento de entrada também sintético, sem PII) → produz XML de
saída real; (c) para cada `FieldToXmlMapping` previsto, extrair o valor no XML real via o
`xpath`/`xmlOccurrence` previsto e comparar contra o valor esperado (calculado manualmente na
fixture, não lido do TXT — evita acoplar o teste ao próprio parser). Divergência = falha da
fixture, não do `LowCodeRunner` (ele é o oráculo).

### 6.3 Métricas de cobertura a reportar

- % de fixtures com resolução `authoritative` que bateu exatamente com o XPath/ocorrência real.
- % de fixtures corretamente marcadas `best-effort` (nenhuma marcada `authoritative` por engano
  — falso positivo de confiança é o erro mais caro aqui, pior que falso `best-effort`).
- Lista de `mappingKind` × dimensão da matriz com 0% de cobertura de teste (gap explícito, não
  silencioso).
- Toda divergência não eliminável por ajuste do algoritmo → documentar como limitação conhecida
  no mesmo padrão já usado em #139 §7.1 (não esconder, não forçar `authoritative`).

## 7. Modelo de dados final (refinamento do sugerido)

```csharp
public sealed record FieldToXmlMapping(
    string MappingId,
    IReadOnlyList<TxtFieldReference> Sources,   // vazio quando MappingKind == Static
    IReadOnlyList<XmlNodeReference> Targets,
    MappingKind Kind,                            // enum: Direct|Transformed|Concatenated|Static
    Confidence Confidence,                       // enum: Authoritative|BestEffort
    IReadOnlyList<string>? Limitations = null);  // motivo(s) quando BestEffort — nunca null nesse caso

public sealed record TxtFieldReference(
    string LineGuid, string LineName,
    string FieldGuid, string FieldName,
    int LineOccurrence,       // de ParsedField.Occurrence (fragmento físico, IsAggregatedOccurrence=false)
    int StartPosition, int Length);

public sealed record XmlNodeReference(
    string Xpath,
    XmlNodeKind NodeKind,     // Element|Attribute|Text
    int? XmlOccurrence);      // null quando não há repetição no ancestral
```

Diferenças do sugerido pelo dono: `MappingKind`/`XmlNodeKind`/`Confidence` como enum (não string
solta — evita valor inválido silencioso); `Limitations` aditivo para não perder o "porquê" de um
`best-effort` (exigido pela regra "nunca `authoritative` sem evidência, registrar limitação").
`mappingId` mantido como pedido.

**Regra dura, reforçada de novo aqui:** nenhum destes tipos carrega valor real de documento —
`TxtFieldReference`/`XmlNodeReference` são só coordenadas estruturais (GUID/nome/posição/XPath),
nunca `Value`. Consistente com `.claude/rules/security.md` (nunca logar/expor conteúdo de
documento de cliente).

## 8. Divisão de trabalho para despacho

| # | Trabalho | Dono | Depende de |
|---|---|---|---|
| 1 | `XmlLayoutStructureParser` (§2.1) — parser do layout XML de destino a partir de `TargetLayoutGuid`, produz árvore `XmlLayoutNode` | `@lp-parser-llm` | Confirmar contra amostra real (fora do Git) se o Sysmiddle expõe estrutura XML de destino própria ou se precisa do fallback XSD SEFAZ — decisão bloqueante de design, não de implementação |
| 2 | Construção de XPath absoluto + cache `TargetLayoutGuid→XmlLayoutNode[]` (§2.2-2.3) | `@lp-backend-dev` | #1 |
| 3 | Classificador `mappingKind` sobre `StructuredRule` (§3) | `@lp-parser-llm` | Nenhuma (já pode começar — `StructuredRule` já existe) |
| 4 | Resolução `lineOccurrence`→`xmlOccurrence` (§4) | `@lp-parser-llm` | #1 (precisa de `MaxOccurs` do lado XML) |
| 5 | Motor de composição `FieldToXmlMapping[]` (junta 1-4, aplica critério `authoritative`/`best-effort` §5) | `@lp-parser-llm` | #1-4 |
| 6 | Endpoint HTTP (`/fieldMappings` ou equivalente, conectar ao `MappingStructureService` já no DI) | `@lp-backend-dev` | #5 |
| 7 | Fixtures sintéticas (20 casos, §6.1) + harness de comparação contra `LowCodeRunner` real | `@lp-qa` | #5 (pode desenhar fixtures em paralelo, mas só roda comparação depois) |
| 8 | Execução da validação comportamental completa + relatório de métricas (§6.3) | `@lp-qa` | #6, #7 |
| 9 | Documentar contrato/Swagger do endpoint novo | `@lp-doc` | #6 |
| 10 | Formalizar issues #140 (sub-tarefas 1-6 acima) e acompanhar #141 (pathway TCL/XSL) | `@lp-pm` | Este documento |

Bloqueio real de sequenciamento: **item 1 precisa de uma decisão de dono** (Sysmiddle expõe XML
de destino estruturado, ou só o XSD SEFAZ está disponível?) antes de #2-6 começarem — mesma
categoria de bloqueio já registrada em #139 §5 (amostra real fora do alcance de qualquer agente).
Enquanto isso, itens 3 e 7 (fixtures) podem começar em paralelo sem depender de #1.
