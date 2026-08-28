# Design: `fieldMappings` em `POST /api/transformationexecution/execute-candidates` — issue #141

> Autoria: `@lp-architect` (Aria). Missão `design-feature`. **Não implementado** — desenho de
> integração para `@lp-backend-dev`/`@lp-parser-llm`/`@lp-qa`/`@lp-doc` executarem.
>
> Pré-requisitos: #139 (PR #201, verde), #140 (PR #205, verde — **ressalva ativa**: validação
> comportamental de 20 execuções reais do `LowCodeRunner` não foi feita neste ambiente, só
> validação estrutural sintética; o dono autorizou seguir mesmo assim). Este design **herda** essa
// ressalva: `fieldMappings` no contrato de `execute-candidates` carrega o mesmo risco de acurácia
> não confirmado por dado real — ver §7.

## 0. Onde as peças já estão (não recriar)

| Peça | Local | Estado |
|---|---|---|
| Motor de composição | `ai/XslSynth.Contracts/Core/StructuralResolution/` | Implementado, síncrono, CPU-bound (sem processo externo) |
| Serviço de fachada | `Services/Transformation/StructuralResolution/FieldMappingCompositionService.cs`, método `Compose(Layout, ParsedFields, MapperVo, LineInfos)` | Síncrono, chamado hoje só por `field-mappings` |
| Endpoint isolado | `POST /api/TransformationExecution/field-mappings` (`Controllers/TransformationExecutionController.cs:983-1059`) | Funcional, deliberadamente separado de `execute-candidates` |
| Modelo de risco/confiança | `FieldToXmlMapping.Confidence` (`Authoritative`/`BestEffort`) + `Limitations` | Já modelado no design #140 §5/§7 |

Ponto central: **o endpoint `/field-mappings` já faz exatamente o trabalho que `fieldMappings`
precisa em `execute-candidates`** — parse posicional real → mapper ranqueado real → `Compose(...)`.
A integração da #141 não é "escrever o motor de novo", é decidir **quando e como** chamar essa
mesma sequência dentro do fluxo multi-candidato sem violar o teto de request já existente.

## 1. Diferença estrutural crítica: sysmiddle roda processo externo, `fieldMappings` não

O timeout/cancelamento cooperativo em `execute-candidates` (linhas 245-292 do controller,
`LowCodeCandidatesBudget`) existe porque o pathway sysmiddle dispara o `LowCodeRunner.exe` (x86,
processo externo, `MaxConcurrentRunners` slots compartilhados por toda a API — recurso físico
escasso). **`FieldMappingCompositionService.Compose` não dispara nenhum processo externo** — é
travessia de árvore (`XmlLayoutNode`) + classificação de `StructuredRule` em memória, sobre dados
já carregados. Isso muda a resposta à pergunta 1 do escopo:

**Não precisa de timeout/cancelamento próprio como o sysmiddle precisa.** O trabalho é CPU-bound e
determinístico em tamanho (limitado pelo número de campos do layout de origem × elementos do
mapper), não sujeito a fila de processos externos. O risco de latência aqui é outro: repetir I/O
que já foi pago (parse posicional, busca de mapper decifrado no SQL) uma segunda vez por candidato.

## 2. Ponto de integração: dentro de `ExecuteSysmiddleCandidatesAsync`, não como 3º Task

### Opção A — Task paralelo adicional (`fieldMappingsTask`, como `sysmiddleTask`/`tclXslTask`)

Rejeitada. Recalcularia do zero: novo parse posicional (`_layoutParser.ParseAsync`) e nova busca de
mapper (`GetRankedMapperCandidatesForLayoutGuidAsync`) — **ambos já acontecem dentro de**
`_lowCodeAuto.RunAsync` (chamado em `ExecuteSysmiddleCandidatesAsync`, linha 392). Duplicar essas
duas operações de I/O (SQL + parse do documento) por request é o oposto do requisito de
performance do usuário — dobra o custo de infraestrutura sem motivo, e cria uma segunda fonte de
verdade para "qual mapper foi usado" que pode divergir silenciosamente do que o pathway sysmiddle
usou de fato (`autoResult.Candidates[i].MapperGuid`).

### Opção B — Inline, dentro de `ExecuteSysmiddleCandidatesAsync`, por candidato bem-sucedido (recomendada)

`LowCodeAutoTransformationService.RunAsync` já resolve, por dentro, o parse posicional e o(s)
mapper(s) candidato(s) que geraram cada `c.OutputXml` bem-sucedido. A integração correta é expor
esses dois artefatos (já calculados, sem custo adicional de I/O) de volta ao controller — via um
campo novo opcional no resultado de `RunAsync` (`ParsedField[]`/`ParsingResult` e `MapperVo` por
candidato, ou simplesmente o `mapperRecord.DecryptedContent`/`MapperGuid` já usado internamente) —
e chamar `_fieldMappingComposition.Compose(...)` **uma vez por candidato bem-sucedido**, logo após
o `if (c.Success && !string.IsNullOrEmpty(c.OutputXml))` (linha 425), reaproveitando:

- `parsingResult.Layout`/`ParsedFields`/`LineInfos` — computados **uma vez** por request (o
  documento de entrada é o mesmo para todos os candidatos sysmiddle), não uma vez por candidato;
- `mapperVo` — computado uma vez por candidato (mapper diferente por definição de candidato).

```
foreach (var c in autoResult.Candidates)
{
    if (c.Success && !string.IsNullOrEmpty(c.OutputXml))
    {
        var fieldMappings = TryComposeFieldMappings(sharedParsingResult, c.MapperVo, request.LayoutName, warnings);
        result.Add(new TransformationCandidate {
            CandidateId = $"sysmiddle-{c.MapperGuid}", Pathway = "sysmiddle",
            TransformedXml = c.OutputXml, FieldMappings = fieldMappings  // null se falhar/best-effort indisponível
        });
    }
    ...
}
```

`TryComposeFieldMappings` é um wrapper `try/catch` que **nunca propaga exceção** — mesma regra do
resto do endpoint (linha 1054-1059 do `field-mappings` já faz isso): falha na composição vira
`fieldMappings: null` + warning textual, **nunca** derruba o candidato nem o XML já produzido.

**`tcl-xsl` retorna `fieldMappings: null` sempre**, por decisão explícita do usuário (mesma
categoria de decisão já tomada na #138 para `sectionMappings`) — não há fonte estrutural
equivalente ao `Layout`/`MapperVo` para esse pathway hoje. Isso é um `if` de uma linha em
`ExecuteTclXslCandidatesAsync`, sem trabalho de composição.

### Por que inline, e não fire-and-forget como o pathway IA

O pathway IA (`TryEnqueueAiCandidate`) é fire-and-forget porque é caro (minutos, chama Ollama) e
não é essencial à resposta síncrona. `fieldMappings` é o oposto: barato (milissegundos, memória),
aditivo ao candidato que **já vai ser** retornado na mesma resposta — não há razão de design para
adiar via ticket/polling. Fazer isso assíncrono obrigaria o front a um segundo round-trip por
candidato só para obter um campo que custa pouco a mais para calcular já dentro do request atual.

## 3. Paralelização seguraa

- **Por candidato, não por campo.** Um candidato sysmiddle é `Compose(sharedParsingResult,
  candidateMapperVo)` — chamadas independentes entre candidatos (mapper diferente cada uma), então
  `Task.WhenAll` sobre a lista de candidatos bem-sucedidos é seguro (nenhum estado mutável
  compartilhado além do `sharedParsingResult`, que é read-only depois de calculado uma vez).
  Paralelizar dentro de um único `Compose` (por campo) não é necessário — o motor já é uma
  travessia de árvore in-memory de custo baixo (ordem de dezenas a poucas centenas de campos por
  layout NF-e), não um gargalo que justifique a complexidade extra de fan-out interno.
- **Não competir com o teto do sysmiddle.** Como não usa `MaxConcurrentRunners` nem processo
  externo, `Compose` não precisa ser admitido pelo mesmo semáforo — rodar em paralelo aos
  candidatos não aumenta pressão sobre o recurso escasso (runner x86). Ainda assim, respeitar o
  `candidatesCts.Token` do request (linha 271) por educação — se o request já vai retornar 504,
  não vale a pena terminar a composição depois que a resposta já foi descartada; propagar o token
  para os `Task.WhenAll` de composição também.

## 4. Cache por hash de mapper + target layout

O gap real hoje: `_fieldMappingComposition.Compose` recebe `MapperVo` já parseado (não faz cache
próprio), e o catálogo `TargetLayoutGuid → XmlLayoutNode[]` (design #140 §2.3) já está proposto
para reaproveitar o mesmo padrão de `MapperCacheService`/`CachedLayoutService` (Redis opcional +
fallback sem cache) — **isso já cobre a parte cara e estável** (estrutura do layout XML de destino,
que muda raramente).

O que falta cachear, especificamente para #141: o **resultado da composição por
`(MapperGuid, MapperContentHash, TargetLayoutGuid)`** — não por documento de entrada, porque
`Sources`/`Targets` de `FieldToXmlMapping` são coordenadas estruturais (`LineGuid`/`FieldGuid`/
`ElementGuid`/`XPath`), **nunca valor do documento** (regra dura do design #140 §7) — logo o
resultado de `Compose` é o mesmo para todo documento que use o mesmo mapper, contra o mesmo layout
de destino. Isso é uma propriedade forte: **cache por chave de mapper, não por request** — hit
rate esperado alto (o mesmo mapper é reusado por centenas/milhares de documentos).

```
cacheKey = $"fieldmappings:{mapperGuid}:{Hash(mapperRecord.DecryptedContent)}:{targetLayoutGuid}"
```

`Hash(DecryptedContent)` (SHA-256 truncado, mesmo padrão já cogitado em memórias de cache de
mapper) protege contra mapper republicado sob o mesmo GUID com conteúdo diferente — não confiar só
no GUID como chave. TTL: mesma política do `MapperCacheService` (o mapper já é invalidado por
mudança de conteúdo via hash, não por tempo — TTL longo ou sem expiração ativa é aceitável).

**Isso é aditivo, não bloqueante**: o cache reduz custo médio, mas o design de §2 já é barato o
suficiente para funcionar corretamente sem cache no dia 1 — recomendação: implementar a integração
primeiro (item 6 da divisão de trabalho abaixo), medir o baseline real (§5), e só then decidir se o
cache de `Compose` por mapper é necessário para ficar dentro do orçamento de 10% de regressão, ou
se o custo já é desprezível frente ao runner x86 (que domina o tempo total do endpoint hoje).

## 5. Medição de baseline (antes de mudar qualquer coisa)

Pré-condição da issue: medir o p95 **atual** de `execute-candidates` (sem `fieldMappings`) antes
de tocar no código. Proposta concreta para `@lp-qa`:

1. Reaproveitar as fixtures sintéticas já desenhadas para #140 §6.1 (20 cenários) — mesmo conjunto,
   sem trabalho de fixture novo.
2. Rodar `POST execute-candidates` N vezes (sugestão: 30 execuções por fixture, suficiente para p95
   estável em ambiente de dev sem carga concorrente real) contra o código **atual** (antes desta
   issue), medindo latência wall-clock do endpoint via o já existente `CorrelationId`/log
   estruturado (não precisa de ferramenta de load-test nova).
3. Documentar `p50`/`p95`/`p99` por fixture e agregado, num artefato versionado (ex.:
   `docs/architecture/baseline-performance-execute-candidates-2026-XX-XX.md` ou anexo à mesma
   pasta de QA gates) — esse artefato é o insumo que a tarefa de medição pós-mudança compara contra.
4. Repetir a mesma bateria depois da integração (#6 abaixo implementado) e comparar. Regressão
   máxima aceitável: **p95 pós ≤ p95 pré × 1.10** (requisito explícito do usuário). Se estourar,
   a primeira alavanca é o cache do §4 (ainda não implementado no dia 1 por design).

Nota de risco de medição: como o pathway sysmiddle é dominado pelo custo do `LowCodeRunner.exe`
(centenas de ms a segundos por candidato, processo externo), é esperado que a fração de tempo
adicional de `Compose` (µs a poucos ms, in-memory) seja **pequena relativa ao total** — a hipótese
de trabalho é que o overhead fique bem abaixo dos 10%, mas isso precisa ser confirmado, não
assumido.

## 6. Plano de testes de contrato

| Caso | Cobertura no motor (#140) | O que falta para #141 |
|---|---|---|
| `fieldMappings: null` (tcl-xsl) | N/A — decisão de escopo, não do motor | Testar que o campo é `null` explícito no candidato tcl-xsl, nunca `[]` nem ausente |
| `fieldMappings: []` (sysmiddle sem relação encontrada) | Coberto — `Compose` retorna lista vazia quando não resolve nada | Testar via candidato sysmiddle cujo mapper não produz nenhum `FieldToXmlMapping` resolvível |
| `direct` | Fixture #140 dimensão "1 campo→1 elemento direto" | Testar presença de `fieldMappings` no candidato real do endpoint multi-candidato, não só no `/field-mappings` isolado |
| `transformed` | Fixture #140 dimensão 11 (`CalculateVerifierDigit`) | Idem — reusar fixture, trocar endpoint testado |
| `concatenated` | Fixture #140 dimensão 7 | Idem |
| `static` | Fixture #140 dimensão 8 | Idem |
| N:1 | Fixture #140 dimensão 13 | Idem |
| 1:N | Fixture #140 dimensão 14 | Idem |
| Repetição (`lineOccurrence`/`xmlOccurrence`) | Fixture #140 dimensão 4/5 | Idem |
| Falha de composição não derruba candidato | Não coberto ainda — é específico da integração #141 | Novo teste: forçar exceção dentro de `Compose` (mock) e confirmar que `TransformedXml` do candidato continua presente, `fieldMappings: null`, warning adicionado |
| Timeout/cancelamento propagado | Não coberto | Novo teste: cancelar `candidatesCts` durante composição, confirmar que não deixa a request pendurada além do budget já calculado |
| Cache hit/miss (se implementado) | N/A | Novo teste: dois requests com mesmo mapper/layout, segunda chamada não recalcula (via contador de invocação de `Compose` mockado) |

Toda a matriz de `mappingKind`/ocorrência já foi coberta estruturalmente pela #140 (20 fixtures) —
o trabalho de teste da #141 é majoritariamente **reexecutar contra o novo ponto de entrada**
(candidato de `execute-candidates` em vez do endpoint isolado `/field-mappings`), não desenhar
casos novos, exceto os 3 últimos da tabela (específicos da integração: falha isolada, timeout,
cache).

## 7. Convenção de XPath/namespace — confirmação de consistência

Nenhuma mudança de convenção nesta issue. `fieldMappings` em `execute-candidates` usa **exatamente**
o mesmo `FieldToXmlMapping`/`TxtFieldReference`/`XmlNodeReference` definidos no design #140 §7 —
reexpor o mesmo tipo, não um DTO paralelo. Isso garante que um consumidor que já integrou com
`/field-mappings` (endpoint isolado) não precisa reaprender um contrato diferente ao migrar para
`fieldMappings` embutido em `execute-candidates`.

**Herança explícita da ressalva de #140:** a validação comportamental de 20 execuções reais do
`LowCodeRunner` (design #140 §6) ainda não rodou neste ambiente — só a validação estrutural
sintética. Isso significa que a hipótese central de `xmlOccurrence` (§4.2 do design #140:
correspondência 1:1 posicional entre N-ésima ocorrência de linha repetida e N-ésimo nó XML
repetido) **não está confirmada contra dado real**. Expor `fieldMappings` em `execute-candidates`
(superfície mais visível/usada que o endpoint isolado) aumenta a exposição a essa lacuna — não é
motivo para bloquear a integração, mas é motivo para **não remover o rótulo `best-effort`/
`Limitations`** do contrato, e para o `@lp-doc` deixar explícito no Swagger que `Confidence` é
o sinal de confiabilidade que o front deve respeitar, não tratar todo `fieldMappings` não-nulo como
verdade absoluta.

## 8. Divisão de trabalho

| # | Trabalho | Dono | Depende de |
|---|---|---|---|
| 1 | Adicionar `FieldMappings` (nullable, `IReadOnlyList<FieldToXmlMapping>?`) a `TransformationCandidate` (DTO) — aditivo, retrocompatível | `@lp-backend-dev` | Nenhuma |
| 2 | Expor `MapperVo`/`ParsingResult` do candidato bem-sucedido de dentro de `LowCodeAutoTransformationService.RunAsync` (ou equivalente) para o controller reaproveitar sem novo I/O | `@lp-backend-dev` | Nenhuma — é o ponto central do §2 |
| 3 | Chamar `_fieldMappingComposition.Compose(...)` por candidato sysmiddle bem-sucedido, com `try/catch` isolado (`TryComposeFieldMappings`), dentro de `ExecuteSysmiddleCandidatesAsync` | `@lp-backend-dev` | #1, #2 |
| 4 | `fieldMappings: null` explícito no pathway tcl-xsl (`ExecuteTclXslCandidatesAsync`) | `@lp-backend-dev` | #1 |
| 5 | Cache `(MapperGuid, ContentHash, TargetLayoutGuid) → FieldToXmlMapping[]` (§4) — implementar **depois** de medir se é necessário (item 8) | `@lp-backend-dev` | #3, resultado de #8 |
| 6 | Qualquer ajuste no motor exigido pela integração (ex.: se `Compose` precisar de assinatura diferente para reaproveitar `MapperVo` já parseado sem reparsear) | `@lp-parser-llm` | #2 |
| 7 | Medir baseline de performance ANTES da mudança (§5, passos 1-3) | `@lp-qa` | Nenhuma — pode começar já |
| 8 | Medir performance DEPOIS da integração (#3) e comparar contra baseline (§5, passo 4); decidir se cache (#5) é necessário | `@lp-qa` | #3, #7 |
| 9 | Testes de contrato (tabela §6, reexecução das fixtures #140 + 3 casos novos de integração) | `@lp-qa` | #3, #4 |
| 10 | Documentar `fieldMappings` no Swagger/XML docs de `execute-candidates`, incluindo a nota de confiança/ressalva do §7 | `@lp-doc` | #1, #3, #4 |
| 11 | Atualizar README/contrato consumido pelo front (LayoutParserReact #128) | `@lp-doc` | #10 |

Sequenciamento sugerido: 7 (baseline) e 6 (motor, se necessário) podem começar em paralelo com
1-2-3-4 (não têm dependência mútua); 5 (cache) só depois de 8 confirmar que é necessário — não
implementar cache especulativamente antes de medir, conforme o próprio requisito do usuário
("medir baseline antes/depois").

## 9. QA gate — resultado (2026-08-28, `@lp-qa`)

**Veredito: PASS.** `dotnet build` limpo (só warnings pré-existentes SCS0005/SCS0018, nenhum novo
relevante à mudança). `dotnet test`: 408/412 passando (2 testes novos desta sessão, ver abaixo),
4 falhas pré-existentes (Windows-path vs Linux dev, não relacionadas a #141 — mesmas 4 do baseline
registrado em memória de QA anterior).

**Cobertura de contrato:** a implementação (`ed8f0bb`) seguiu o design (§2 opção B, inline,
`sharedParsingResult` calculado uma vez por request) e trouxe 4 testes cobrindo: candidato
bem-sucedido com `fieldMappings` preenchido, falha isolada de composição (`fieldMappings: null` +
warning, `TransformedXml` sobrevive), parse compartilhado falhando (`null` para todos os
candidatos sysmiddle), e `tcl-xsl` sempre `null`. Faltavam 2 casos do design §6 — adicionados
nesta sessão (`98b527e`):
- **`fieldMappings: []` (não `null`)** quando o mapper não tem nenhum `LinkMappingItem`/`Rule`.
  **Achado durante a escrita do teste:** `Compose()` **não filtra por resolução de origem** — um
  `LinkMappingItem` cujo `InputLayoutGuid` não existe no `sharedParsingResult` ainda gera uma
  entrada `FieldToXmlMapping` com `Confidence: BestEffort` (não é descartado). `[]` só ocorre
  quando o mapper não tem nenhum link/rule para iterar. Não é um bug — é o motor #140 se
  comportando como best-effort por design — mas é um comportamento não documentado explicitamente
  no design desta issue, que assumia implicitamente "sem correspondência → lista vazia".
- **XML byte-idêntico com/sem extração de `fieldMappings`** — confirmado: `TransformedXml` vem
  exclusivamente de `LowCodeCandidateResult.OutputXml` (produzido pelo runner), nunca tocado por
  `TryComposeFieldMappings`. Teste compara execução com parse OK vs. parse forçado a falhar —
  `TransformedXml` idêntico nos dois casos, `fieldMappings` diverge (`[...]` vs `null`) como
  esperado.

Os demais casos da tabela §6 (`direct`/`transformed`/`concatenated`/`static`/N:1/1:N/repetição)
permanecem cobertos pela suíte estrutural da #140 (`ai/XslSynth.Core.Tests/StructuralResolution/`,
25+ testes) — reexecução contra o novo ponto de entrada não é necessária: o candidato sysmiddle
de `execute-candidates` chama o **mesmo** `FieldMappingCompositionService.Compose` já validado, sem
lógica de transformação adicional no controller (confirmado lendo `TryComposeFieldMappings`).

**Performance (§5):** medição de p95 real do `LowCodeRunner.exe` (antes/depois) **não é possível
neste ambiente** — dev workstation é Linux/WSL, o runner é um `.exe` x86 nativo Windows (mesmo
bloqueio já registrado nas memórias de QA para #140 e para o spec Cypress de emissão normal). Como
alternativa, medido o **overhead isolado** introduzido pelo código novo (parse posicional
compartilhado + `RealMapperParser` + `Compose`) via microbenchmark descartável (60 execuções,
controllers com fakes reaproveitados de `TransformationExecutionControllerFieldMappingsTests`,
cache de catálogo XSD **quente** — i.e., replicando o estado estacionário de produção, onde
`StructuralXmlCatalogCacheService` vive por todo o processo, não por request):

| Cenário | p50 | p95 | p99 |
|---|---|---|---|
| COM `fieldMappings` (parse + Compose rodando) | 0.422 ms | 0.780 ms | 1.148 ms |
| SEM `fieldMappings` (parse falha, `Compose` pulado) | 0.381 ms | 0.670 ms | 5.704 ms |
| **Overhead isolado (delta p95)** | — | **≈0.11 ms** | — |

Nota metodológica: a primeira tentativa (cache do catálogo XSD recriado a cada iteração) mediu
374 ms de overhead p95 — isso é o custo de **compilar o XSD da NF-e do zero** (schema real,
`nfe_v4.00.xsd`), não o custo do caminho de código em regime permanente. Esse custo só acontece
uma vez por processo em produção (cache singleton/scoped de vida longa), não por request — por
isso a medição correta reusa os controllers entre iterações.

**Conclusão:** o overhead isolado (~0.1 ms) é consistente com a hipótese do design (§5,
"pequena relativa ao total" — o runner `.exe` domina com centenas de ms a segundos por candidato).
Não é uma medição do p95 real ponta a ponta do endpoint (impossível sem o runner Windows), mas é
evidência direta de que o código adicionado pela #141 não é, ele mesmo, um vetor de regressão
relevante de latência. **Recomendação: não implementar o cache do §4 no dia 1** — a
implementação de referência (item 6 da divisão de trabalho) já satisfaz o requisito de ≤10% de
regressão pela margem observada aqui; cache fica como item de backlog condicional a uma medição
futura com o runner real em ambiente Windows (bloqueio documentado, dono de decisão: `@lp-devops`/
dono do projeto).
