# Contrato aditivo: linha declarada vazia, progresso de processamento e degradação posicional

**Não implementado** — desenho de arquitetura (`@lp-architect`). Complementa
`diagnostico-bug-informacoesparaedi-e-id-ocorrencia-2026-08-21.md` (não duplica: aquele já
cobre `OccurrenceCount`/`IsAggregatedOccurrence`/Bug A de `Length`; este cobre os 3 pedidos
novos do dono, incluindo o sintoma **novo e mais grave** da LINHA006).

## 1. Linha declarada porém vazia

Hoje o parser (`ParseTextWithSequenceValidation` + `ParseLineFields`,
`Services/Implementations/LayoutParserService .cs`) só produz 3 estados observáveis pelo front,
nenhum deles explícito:

| Estado real | Como aparece hoje |
|---|---|
| Linha não reconhecida (nenhum `LineElement` bate) | vira `unidentifiedLines` — só log técnico, **não chega ao front** |
| Linha reconhecida, abaixo de `MinimalOccurrence` | `ValidateLineOccurrences` — sinalizado em outro lugar do contrato (não neste escopo) |
| Linha reconhecida, presente, mas com `Content` vazio/branco | Indistinguível de "campo com erro de parsing" — cada `ParsedField` carrega seu próprio `Status` (`ok`/`warning`/`error`), mas nada no nível de **linha** diz "esta ocorrência existe e foi identificada, só que não tem conteúdo" |

**Proposta (aditiva, em `LineInfo` — o único lugar hoje que representa "linha" como unidade,
`Models/Entities/LineInfo.cs`):**

```csharp
public bool IsDeclaredEmpty { get; set; }   // true quando a linha foi identificada
                                             // (matchingLineConfig != null) e Content é
                                             // vazio/whitespace após padding/trim
```

Calculado em `ParseTextWithSequenceValidation`, no ponto em que `matchingLineConfig != null`
(linha ~328 do arquivo) e antes de chamar `ParseLineFields`: `IsDeclaredEmpty =
string.IsNullOrWhiteSpace(currentLine.Trim())`. Não interfere no cálculo de `Status`/`Length`
de cada campo — é um sinal de linha, ortogonal ao sinal de campo. Mantém o princípio "aditivo,
não substitutivo" já usado no fix do Bug A/B.

Se `LineInfo` não for hoje o objeto que chega ao front (confirmar com `@lp-backend-dev` o
DTO real de resposta de `/api/parse/upload` — pode ser um `LineResult`/agregação diferente),
o mesmo campo booleano deve ser adicionado no DTO de linha que efetivamente serializa a
resposta, não necessariamente em `LineInfo`.

## 2. Progresso de processamento (barra de %)

**Achado-chave: já existe a fundação certa, não precisa infra nova.**
`LowCodeTransformationStore` (`Services/Transformation/LowCode/LowCodeTransformationStore.cs`)
já implementa um mecanismo de **ticket + índice + polling**:
`GET /api/parse/transformations/{ticket}` consulta `LowCodeTransformationIndexEntry.Status`,
hoje binário (`ProcessingStatus = "processing"` / `CompletedStatus = "completed"`,
`Models/Transformation/LowCodeTransformationIndex.cs:43-44`).

Isso já resolve a pergunta "polling, SSE ou webhook?" — **polling no ticket existente**, sem
infra nova (sem SignalR/SSE/message broker), consistente com o padrão de resiliência do
projeto (degradar sem depender de conexão persistente; funciona atrás de qualquer proxy/BFF
sem configuração especial de streaming).

**Proposta — trade-off A (fases discretas, recomendada) vs. B (percentual real):**

| | A — Fases discretas | B — Percentual real |
|---|---|---|
| Implementação | Enum de string adicional (`"uploaded"`, `"parsing"`, `"transforming"`, `"completed"`, `"partial"`) | Requer instrumentar cada etapa com peso relativo e granularidade real de progresso interno do low-code runner (processo externo `.exe`/CLR host) |
| Precisão | Aproximada — o front mapeia fase → % fixo (ex.: uploaded=10%, parsing=40%, transforming=70%, completed=100%) | Exata, mas o runner não expõe progresso interno hoje — teria que ser estimado por heurística de tempo, não é "real" de fato |
| Custo/risco | Baixo — reaproveita o índice já existente, campo aditivo | Alto — exige instrumentação no processo externo (`LowCodeTransformationService`/runner), que é justamente a dependência menos confiável do sistema (resiliência é o princípio #1 do projeto) |
| Resiliência | Se o runner cair no meio, fase fica presa em `"transforming"` — front pode timeoutar e cair para erro, sem falso otimismo de %| Se o runner cair, % pode ficar "congelado" em qualquer valor arbitrário — pior UX que uma fase nomeada |

**Recomendação: opção A.** Estender `LowCodeTransformationIndexEntry.Status` com fases
adicionais (mantendo `"processing"`/`"completed"` como hoje para não quebrar consumidores):
`"uploaded"` → `"layout_selected"` → `"parsing"` → `"transforming"` (mapeado ao atual
`"processing"`) → `"completed"`/`"partial"` (já existe partial, conforme comentário em
`LowCodeAutoTransformationService.cs:69-77`). O **front converte fase em % fixo** — não pedir à
API um percentual "real", que criaria acoplamento com internals de um processo externo que já é
o ponto mais frágil do pipeline (resiliência > precisão de UI).

**Fluxo de contrato:** o front já teria (ou precisa ganhar) o ticket no momento do upload —
confirmar com `@lp-backend-dev`/`@lp-parser-llm` se o ticket é retornado síncrono no upload ou
só depois do parse. Se só depois, a fase `"uploaded"`/`"layout_selected"` fica fora do alcance
do ticket-polling (que só existe pós-parse) — nesse caso, essas duas fases iniciais são
*client-side only* (o front já sabe que fez upload e que layout foi selecionado, não precisa
perguntar à API), e o polling do ticket cobre só `"parsing"` em diante. Isso simplifica o
desenho: a API não precisa de um novo endpoint de "status geral", só estender o `Status` que já
existe no índice do low-code.

## 3. Degradação posicional — Sysmiddle aninhado vs. TCL plano

### O que já está diagnosticado (não repetir)
Bug A (`Length = field.LengthField` sempre, nunca o valor real) e Bug B (2 `ParsedField`s por
campo sem sinal de qual é o lógico) — ver diagnóstico de 2026-08-21, dono `@lp-parser-llm`,
ainda **não implementado** (código atual em `Models/Entities/ParsedField.cs` confirma:
sem `OccurrenceCount`/`IsAggregatedOccurrence`).

### O que é novo aqui: LINHA006, `startPosition===endPosition` em TODOS os campos

Isto **não é o mesmo bug**. Bug A produz `Length=500` (o declarado) mesmo quando o valor é
vazio — superestimação. LINHA006 produz o oposto: colapso para largura ~1 em **todos** os
campos da linha, valor vazio em todos. Lendo `ParseLineFields` (linha 902-1037):

- `fieldStart = currentPosition` começa em `CalculateLineOffset(lineConfig, paddedLine, format)`.
- Cada campo avança `currentPosition = endPosition + 1`, onde `endPosition = fieldStart +
  field.LengthField - 1` — ou seja, a posição de cada campo é **cumulativa e depende de
  `field.LengthField` do campo anterior**, lido via `JsonConvert.DeserializeObject<FieldElement>`
  do próprio `LineElement.Elements` (linha 925-936).
- Se `field.LengthField` resolver para `0` (ou `1`) para o primeiro campo de LINHA006 — por
  exemplo, um campo que no Sysmiddle está **aninhado dentro de outro elemento estrutural** e cuja
  desserialização direta de `e` (string JSON solta) não carrega o `LengthField` do nível certo —
  o efeito é exatamente o observado: `fieldStart == endPosition` se propaga por **todos os campos
  subsequentes da linha**, porque cada um herda a posição colapsada do anterior. É consistente
  com "cascata": um único campo com `LengthField` mal resolvido no topo da linha degenera a linha
  inteira, não só aquele campo.

Isso é uma **hipótese fundamentada em código, não confirmada** — falta o `correlationId` do
parse real da LINHA006 para cravar qual campo é o gatilho da cascata e se `LengthField` chega
como `0` ou se `CalculateLineOffset` retorna um offset inicial errado para essa `LineElement`
específica. **Ambos os casos (LINHA081 e LINHA006) dependem de `correlationId` para
investigação de causa raiz pontual** — mas o desenho de contrato abaixo não depende disso.

### Sysmiddle aninhado vs. TCL plano — avaliação pedida pelo dono

O padrão de bug é estruturalmente favorecido pelo modelo do Sysmiddle porque:
1. `FieldElement` é desserializado **a partir de uma string JSON solta por elemento**
   (`lineConfig.Elements.Select(e => JsonConvert.DeserializeObject<FieldElement>(e))`), sem
   contexto do elemento pai — um campo aninhado (campo dentro de sub-elemento estrutural, comum
   no Sysmiddle para representar grupos/ocorrências) perde a garantia de que seu `LengthField`
   foi resolvido *no contexto certo* antes de entrar nesse `Select`.
2. O acúmulo de posição é sequencial e sem parada de segurança: nada no loop detecta "dois
   campos seguidos com a mesma posição inicial" e sinaliza erro — o parser segue calculando
   como se fosse válido, produzindo silenciosamente `Status="error"` só quando a posição sai
   dos limites da linha (`fieldStart >= 0 && endPosition < paddedLine.Length`), não quando o
   comprimento é suspeito (`LengthField == 0`, por exemplo).

**Recomendação para as regras do TCL (novo mapeador, em desenvolvimento):** não herdar (a)
resolução de tamanho de campo via desserialização solta sem contexto do elemento pai — o TCL,
por ser estruturalmente mais plano, tem a oportunidade de resolver `LengthField` **uma vez, no
momento da leitura do layout inteiro**, não campo a campo dentro do loop de parsing; e (b) a
ausência de guarda-corpo — o mapeador TCL deveria tratar `LengthField <= 0` como condição de
erro explícita na origem (falha ao carregar o layout / campo mal declarado), não deixar chegar
ao loop de cálculo de posição onde ele degenera silenciosamente em cascata. Isto é orientação de
design para `@lp-parser-llm` avaliar contra as regras reais do TCL que estão sendo desenhadas —
não é um veredito de que o TCL "resolve sozinho" o problema; a garantia vem de onde e como o
tamanho de campo é resolvido, não da topologia (aninhado vs. plano) por si só.

### Contrato proposto — sinal aditivo, sem acoplar ao mapeador de origem

Conforme pedido explícito do dono, **não** expor `mapperName`. Proposta, em nível de **linha**
(mesmo objeto do item 1, `LineInfo` ou DTO equivalente):

```csharp
public bool PositionalAlignmentFailed { get; set; }
// true quando ≥2 campos consecutivos da mesma ocorrência de linha resolvem para a mesma
// posição inicial (fieldStart colapsado) — sintoma observável, não a causa interna nem o
// mapeador de origem.
```

Detecção proposta (em `ParseLineFields`, sem mudar a lógica de parsing existente): após o loop
de campos, verificar se `parsedFields` dessa ocorrência têm `Start` duplicado entre campos
distintos (`fieldsToProcess.Count > 1 && parsedFields.Where(mesma ocorrência).Select(f =>
f.Start).Distinct().Count() < fieldsToProcess.Count`) → seta `PositionalAlignmentFailed = true`
na `LineInfo`/DTO de linha correspondente. Puramente aditivo — não muda `Status` de campo nem
`Length`, só adiciona o sinal de linha que o front pode usar para exibir "atenção: alinhamento
posicional falhou nesta linha" sem precisar inferir isso comparando `Start`/`Length` no cliente.

Isso também **generaliza** — cobre qualquer causa futura de colapso posicional (não só a
hipótese do LINHA006), incluindo eventuais regressões do TCL, sem exigir novo campo por
mapeador.

## Próximos passos e donos

| # | Ação | Dono |
|---|---|---|
| 1 | Confirmar `correlationId` dos parses de LINHA081/LINHA006 reportados | **Dono do projeto** (pendente — bloqueia só a causa raiz pontual, não o desenho) |
| 2 | Implementar `IsDeclaredEmpty` em `LineInfo`/DTO de linha | `@lp-parser-llm` |
| 3 | Implementar fases discretas em `LowCodeTransformationIndexEntry.Status` (`uploaded`/`layout_selected`/`parsing`/`transforming`/`completed`/`partial`) e confirmar onde o ticket é retornado ao front (upload vs. pós-parse) | `@lp-backend-dev` |
| 4 | Implementar `PositionalAlignmentFailed` (detecção de `Start` duplicado dentro da mesma ocorrência) — sequenciar **junto** com o fix do Bug A/B já diagnosticado (mesmo arquivo, mesma função) | `@lp-parser-llm` |
| 5 | Validar hipótese de causa raiz da LINHA006 (campo aninhado com `LengthField` mal resolvido) assim que os `correlationId`s chegarem | `@lp-parser-llm` |
| 6 | Ao desenhar as regras do TCL, resolver `LengthField` uma vez no carregamento do layout (não campo a campo no loop de parsing) e tratar `LengthField<=0` como erro explícito | `@lp-parser-llm` |
| 7 | Atualizar Swagger/README com os 3 campos aditivos (`IsDeclaredEmpty`, fases de `Status`, `PositionalAlignmentFailed`) | `@lp-doc` |
| 8 | Cobertura de teste de regressão para os 3 sinais novos + o caso LINHA006 assim que reproduzível | `@lp-qa` |
| 9 | Formalizar como issues rastreáveis (bug LINHA006 é achado novo, ainda sem issue) | `@lp-pm` |
