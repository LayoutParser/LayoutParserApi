# Diagnóstico — `InformacoesParaEDI` (LINHA081) com tamanho máximo/valor vazio + proposta de ID de ocorrência

**Reportado por:** LayoutParserReact. **Layout:** `LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c.xml` (MQSeries).
**Não implementado** — investigação apenas.

## Causa raiz (dupla, confirmada em código)

### Bug A — `Length` sempre o valor declarado no XML, nunca o real (a causa do sintoma reportado)

`Services/Implementations/LayoutParserService .cs:1020`, dentro de `ParseLineFields` (uma
chamada por ocorrência física de linha):

```csharp
parsedFields.Add(new ParsedField {
    ...
    Length = field.LengthField,   // ❌ sempre 500 (o declarado no XML), nunca value.Length
    Value  = value,                // já passou por ApplyAlignment/trim
    ...
});
```

`InformacoesParaEDI` é declarado com `LengthField=500` no layout (`Elements/.../Name>
InformacoesParaEDI</Name><LengthField>500</LengthField>`, linha ~14790 do XML). Toda ocorrência
física relata `Length=500` — mesmo quando `Value` é vazio (ocorrência além do conteúdo real do
documento) ou quando o conteúdo real é bem menor. É um bug independente do já mapeado em
`line-repetition-position-bug.md`.

### Bug B — o front recebe 2 `ParsedField`s por campo, sem forma de escolher qual mostrar

O fix da issue #37 (`AggregatePositionalGroupRepetitions`, mesmo arquivo, linha 417) já resolve
`LINHA081` corretamente: agrega os fragmentos físicos (`Occurrence=1..N`) num `ParsedField`
adicional com `Occurrence=0`, `Length = aggregatedValue.Length` (**correto** — 81 no exemplo bom).
Mas ele é **aditivo**: `parsedFields.AddRange(aggregatedFields)` (linha 481) — os fragmentos
brutos (`Occurrence=1..N`, com o Bug A) continuam na lista. `Filler`/`Sequencia` do mesmo
`LineElement` também entram na agregação (o loop agrupa por `FieldName` dentro da linha marcada
`IsPositionalGroupRepetition`, não só o campo específico), por isso o `Filler` "correto" aparece
degenerado (`Pos: 510-510`, `Len: N/A`).

**Nada no `ParsedField` diz ao consumidor qual das N+1 entradas por `FieldName` é "a" que deve ser
exibida.** O exemplo "incorreto" do bug é um fragmento bruto (`Occurrence>0`) com o Bug A ativo;
o exemplo "correto" é o agregado (`Occurrence=0`). O front, sem esse sinal, ora renderiza um ora
outro — condizente com "em algumas ocorrências" do relato.

## Veredito sobre o ID de ocorrência — vale a pena, e resolve os dois bugs juntos

Sim. Um campo aditivo tipo `occurrenceIndex`/`occurrenceCount` (ou `isFirstOccurrence`/
`isLastOccurrence`) no `ParsedField` (`Models/Entities/ParsedField.cs`) não é só diagnóstico —
é a peça que falta para o consumidor saber determinar, sem heurística, qual entrada é o valor
lógico final quando há agregação por `IsPositionalGroupRepetition`. Hoje o front não tem como
distinguir "fragmento físico" de "valor agregado" a não ser checando `Occurrence==0` — frágil e
não documentado no contrato.

**Shape proposto (aditivo, `ParsedField.cs`):**

```csharp
public int OccurrenceIndex { get; set; } = 1;      // já existe como Occurrence — renomear é breaking; manter Occurrence
public int OccurrenceCount { get; set; } = 1;       // total de ocorrências físicas da LineElement pai
public bool IsAggregatedOccurrence { get; set; }    // true só para o ParsedField gerado por AggregatePositionalGroupRepetitions (Occurrence=0)
```

Não renomear `Occurrence` (quebraria consumidores existentes — `ValidateLineOccurrences`, testes
de regressão, front). Adicionar `OccurrenceCount` (constante por `LineName` dentro do resultado,
fácil de preencher em `ParseLineFields`/`AggregatePositionalGroupRepetitions`, ambos já têm o
`lineFields`/ocorrências em mãos) e `IsAggregatedOccurrence` (booleano explícito, mais direto que
o front reinventar "Occurrence==0 significa agregado").

Isso **conecta diretamente** com `line-repetition-position-bug.md`: os dois deveriam ser
corrigidos juntos, na mesma mudança — `IsAggregatedOccurrence`/`OccurrenceCount` só fazem sentido
lidos ao lado da lógica de `IsPositionalGroupRepetition`/`AggregatePositionalGroupRepetitions`,
que é onde o Bug A também precisa ser corrigido (`Length = value.Length`, não `field.LengthField`,
line 1020 — cuidado para não quebrar `status = "warning"` na linha 999, que compara
`value.Length < field.LengthField` e deveria continuar usando o declarado para essa comparação).

## Recomendação de execução

**Dono: `@lp-parser-llm`** (Lia) — é lógica de domínio de parsing posicional, mesmo arquivo do
fix #37 dela.

1. `Services/Implementations/LayoutParserService .cs:1020` — trocar `Length = field.LengthField`
   por `Length = value.Length` no `ParsedField` de cada ocorrência bruta (manter
   `field.LengthField` só na comparação de `status` da linha 999, que já está correta).
2. `Models/Entities/ParsedField.cs` — adicionar `OccurrenceCount` (int, default 1) e
   `IsAggregatedOccurrence` (bool, default false).
3. `ParseLineFields` — preencher `OccurrenceCount` por ocorrência (precisa saber quantas
   ocorrências físicas a `LineElement` teve ao todo; hoje só sabe a atual — requer um segundo
   passe pós-loop, como já faz `ValidateLineOccurrences`, ou popular depois em
   `AggregatePositionalGroupRepetitions`/novo passo simétrico para linhas sem a flag).
4. `AggregatePositionalGroupRepetitions` (linha 457) — no `ParsedField` agregado, setar
   `IsAggregatedOccurrence = true` e `OccurrenceCount = lineFields.Select(f => f.Occurrence)
   .Distinct().Count()`.
5. Atualizar `PositionalFormatRegressionTests.cs` (baseline já existente para `LINHA081`) para
   cobrir `Length` correto no fragmento bruto e os novos campos.
6. Handoff para `@lp-doc`: contrato de `/api/parse/upload` mudou (aditivo) — atualizar Swagger/README.
