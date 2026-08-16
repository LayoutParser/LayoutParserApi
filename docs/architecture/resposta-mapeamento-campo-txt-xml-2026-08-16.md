# Resposta — mapeamento campo TXT ↔ tag XML (PBI #128 / Epic #126)

> Resposta técnica da API .NET ao pedido de `@lp-contract-qa`/front-end sobre viabilidade de
> um contrato `fieldMappings` em `POST /api/transformationexecution/execute-candidates`.
> Estilo: mesmo formato honesto de `resposta-proposta-frontend-progresso-parse-2026-08-14.md`.

## 1. Confirmado no NOSSO código (não é suposição)

### 1.1 Estrutura de `MapperRule`/`MapperVO` — parcialmente confirmada, MAS de uma fonte que não roda em produção

Existe, sim, um parser real de `MapperVO`/`Rule` no repositório — **mas em dois lugares
diferentes, com propósitos diferentes**, e nenhum dos dois é o caminho de runtime:

- `Models/Entities/MapperRule.cs` — parser antigo, 7 campos apenas (`ElementGuid`,
  `Description`, `Sequence`, `Name`, `IsRequired`, `ContentValue`, `CreateOnlyChildren`,
  `IsPrePosRule`, `TargetElementGuid`). Não achei nenhum consumidor dele fora do próprio
  arquivo — parece código órfão.
- `ai/XslSynth.Core/Core/RealMapperParser.cs` + `ai/XslSynth.Core/Model/MapperVo.cs` — parser
  **mais completo e mais recente** (comentários confirmam que foi escrito lendo um `MapperVO`
  real descriptografado), mas vive dentro de `ai/XslSynth` — projeto **standalone, deliberadamente
  desacoplado do runtime Windows-only** (ver `docs/architecture/ia-xslt-synthesis.md` §9 e
  memória `xslsynth-trilha-a-overlap.md`). É o motor de síntese offline de XSLT (Lia/Trilha A),
  não o pipeline que atende `execute-candidates`.

**Conclusão prática:** a lista de campos que o front levantou (`ParentElement`, `IsStaticValue`,
`StaticValue`, `IsPositionalGroupRepetition`, `MinimalOccurrence`/`MaximumOccurrence`, etc.) **não
está confirmada 1:1 no nosso código** — o parser mais completo que temos (`RealMapperParser`)
só popula um subconjunto (`Name`, `Sequence`, `Description`, `ElementGuid`, `TargetElementGuid`,
`TargetType`, `ParentElement`, `ContentValue`, `TargetPath` derivado, `IsRequired`). Os demais
campos que o front viu na amostra real existem no XML do `MapperVO`, mas **nenhum parser hoje no
nosso repo os lê** — não confirmo nem nego o shape completo, só que está fora do que já
implementamos.

### 1.2 `TargetPath` já é resolvido — mas por heurística de texto, não por catálogo GUID→XPath

`RealMapperParser` **não resolve `TargetElementGuid` contra um catálogo** de layout de destino
(seria preciso `TargetLayoutGuid` + um índice GUID→XPath, que não existe). Em vez disso, usa duas
heurísticas, nessa ordem de prioridade:

1. Regex sobre a DSL do `ContentValue`: primeira atribuição `T.<path>` vira o `TargetPath`.
2. Fallback: sufixo após o último `_` do campo `Name` (convenção `Descricao_tag`, ex.:
   `NomeDoMunicipio_xMun` → `xMun`) — só a **folha**, não o caminho completo.

Isso responde diretamente à pergunta 2 do time: **"catálogo GUID→XPath estável" não existe hoje.**
O que existe é uma extração textual da própria DSL — funciona quando a `Rule` escreve
explicitamente `T.<path> = ...`, mas não cobre `Rule`s cujo destino só está no `TargetElementGuid`
sem uma atribuição `T.` correspondente na DSL (existência confirmada pela estrutura, cobertura real
não medida).

### 1.3 Granularidade do mapeamento hoje exposto no contrato HTTP: linha inteira, não campo — DRIFT confirmado, com uma correção de tipo

`@lp-contract-qa` está certo sobre o veredito DRIFT, com um detalhe a corrigir: o campo real
não é exatamente `Record<string,string>` vazio — é
`TransformationCandidate.SegmentMappings: Dictionary<string, string>?` (nullable, populado só às
vezes) alimentado por `MqSeriesToXmlTransformer`, que por sua vez usa um tipo interno
`Dictionary<int, SegmentMapping>` chaveado por **número da linha MQSeries**, não por campo:

```csharp
// Services/XmlAnalysis/MqSeriesToXmlTransformer.cs
result.SegmentMappings = new Dictionary<int, SegmentMapping>();
foreach (var mapping in pipelineResult.SegmentMappings)
    result.SegmentMappings[mapping.Key] = new SegmentMapping {
        MqSeriesLineNumber = mapping.Key,
        MqSeriesSegment = mapping.Value, ...
    };
```

Confirma exatamente o que a investigação read-only do front apontou: granularidade de **linha**,
destino XML **não presente nesse tipo** (`SegmentMapping` guarda segmento MQSeries, não tag XML),
e essa lógica é exclusiva do pathway MQSeries→XML — **desconectada** do que
`POST /api/transformationexecution/execute-candidates` retorna nos dois pathways que ele
realmente usa hoje (sysmiddle e tcl-xsl).

## 2. Fronteira confirmada: fora do nosso alcance

### 2.1 Quem resolve `TargetElementGuid`/`ContentValue` em runtime: `LayoutParserLowCodeRunner.exe`, produto de terceiro

`Controllers/TransformationExecutionController.cs` injeta `LowCodeTransformationService`
(`Services/Transformation/LowCode/LowCodeTransformationService.cs`), que é quem efetivamente
processa o pathway sysmiddle em `execute-candidates`. Esse serviço **invoca o
`LayoutParserLowCodeRunner.exe` como processo externo** — binário fechado do produto Sysmiddle/
AppConnector.

**Sou honesto sobre o limite:** não tenho acesso ao binário/DLL da Sysmiddle para decompilar, e
não vou inventar como o motor interno resolve `I.<Linha>/<Campo>` ou `TargetElementGuid` em
tempo real. O que confirmo é só a fronteira: essa resolução acontece **dentro** do `.exe`, fora do
nosso código-fonte, e hoje o `.exe` devolve o **XML final já transformado** — não um mapa
campo↔tag intermediário. Se essa informação existir internamente no runner, ela não atravessa a
fronteira do processo hoje; extrair isso exigiria ou (a) cooperação do fornecedor da Sysmiddle
para expor um modo de saída anotada, ou (b) reimplementar/instrumentar por fora, o que é
precisamente o "reinventar heurística frágil" que o pedido do front diz querer evitar.

### 2.2 O `RealMapperParser` (síntese offline) não passa pelo `.exe` — mas também não é o dado do runtime real

Vale uma ressalva importante: o `RealMapperParser` em `ai/XslSynth.Core` lê e interpreta o XML do
`MapperVO` **diretamente**, sem passar pelo `.exe` — é por isso que ele consegue produzir
`TargetPath`. Mas ele faz isso **offline, como insumo de treino/geração de XSLT** (Trilha A da
Lia), lendo um `MapperVO` já descriptografado de amostra — não está no caminho de
`execute-candidates` e não roda por request de usuário. Reaproveitar essa lógica de parsing pro
runtime é tecnicamente possível (é C#, está no mesmo repositório/ecossistema), mas é trabalho de
integração real, não "já existe e só falta plugar".

## 3. Respostas às perguntas em aberto

| Pergunta do front | Resposta |
|---|---|
| Granularidade N:1 (1 rule → N campos de entrada)? | Confirmado pela DSL: `ContentValue` pode referenciar múltiplos `I.<Linha>/<Campo>` num único script condicional. Nosso parser (`RealMapperParser`) não extrai essa lista hoje — só o destino (`TargetPath`), não as origens lidas pela DSL. Extrair as origens exigiria parsear a DSL além do regex de `T.<path>=` atual (ver `DslBlockInterpreter.cs`, que já existe para outro propósito — tradução pra XSLT, não catalogação de origens). |
| `TargetElementGuid` resolvível de forma estável via catálogo? | **Não, hoje não existe esse catálogo.** O que existe é heurística textual sobre a DSL (§1.2), que cobre só os casos em que a `Rule` tem atribuição `T.<path>` explícita. Não posso afirmar se `TargetElementGuid` é estável entre execuções/versões — isso é dado interno do produto Sysmiddle, fora do nosso alcance (§2.1). |
| Grupos repetidos — API emite índice de ocorrência ou front infere do XML? | Não implementado hoje em nenhum parser nosso — nem `MapperRule.cs` nem `RealMapperParser` leem `IsPositionalGroupRepetition`/`MinimalOccurrence`/`MaximumOccurrence`. Recomendação de arquitetura: se formos expor `fieldMappings`, o índice de ocorrência deveria vir da API (ela já sabe a ocorrência física real durante o parse do TXT — ver memória `line-repetition-position-bug.md`, aliás um bug ainda aberto nessa mesma área), não ser inferido pelo front a partir do XML de saída — inferir do XML é exatamente a heurística frágil que o pedido quer evitar. |
| Regras com valor estático — `txtFieldGuid: null` explícito? | Sim, se o contrato for construído, esse é o shape correto: `IsStaticValue`/`StaticValue` existem no XML real (confirmado pela amostra do front), mas nenhum parser nosso os lê hoje. Um `FieldToXmlMapping` novo precisaria tratar esse caso como origem nula por design, não como ausência de dado. |
| `tcl-xsl` usa a mesma estrutura de `MapperVO`? | Não confirmado por leitura de código nesta investigação — não localizei, no tempo desta análise, o parser do pathway `tcl-xsl` equivalente ao `LowCodeTransformationService`. Pelo nome (TCL parser posicional + XSLT puro — ver memória `server-assets-inventory.md`), a hipótese mais provável é que seja uma estrutura **diferente**, sem `MapperVO`/`Rule` — o que reforça a recomendação do próprio front: se o contrato novo for adiante, `fieldMappings` deveria ser **opcional/nulo** nesse pathway. Precisa de confirmação de `@lp-parser-llm` antes de fechar o contrato — não é chute, é lacuna de investigação explícita. |

## 4. Veredito de viabilidade

**NÃO VIÁVEL a curto prazo, no shape proposto (`fieldMappings` aditivo em `execute-candidates`
com 1 request/response).** Motivo estrutural, não de esforço de código: a informação de origem
(`I.<Linha>/<Campo>`) → destino (`TargetElementGuid` resolvido) só existe, completa e correta,
**dentro do processo do `.exe` de terceiro** durante a execução real da transformação — e esse
processo hoje devolve XML final, não um mapa intermediário. Não há atalho honesto: ou o
fornecedor da Sysmiddle expõe um modo de saída anotada (fora do nosso controle), ou construímos
uma **segunda via de resolução em paralelo**, reaproveitando o parser offline (`RealMapperParser`)
promovido de ferramenta de síntese pra componente de runtime — o que é viável tecnicamente, mas é
projeto novo (integração + tratamento de DSL multi-origem + catálogo de ocorrência de grupo +
confirmação do pathway `tcl-xsl`), não uma extensão pequena de contrato.

**Recomendação:** tratar como PBI técnico próprio (não sub-tarefa do #128), com escopo mínimo
viável: (1) confirmar shape real do `MapperVO` lendo uma amostra de produção completa; (2)
decidir dono do parser de runtime (promover `RealMapperParser` ou escrever um novo,
específico para essa finalidade); (3) resolver granularidade N:1 e grupos repetidos ANTES de
desenhar o contrato — mudar o shape depois de publicado quebra o front. `@lp-parser-llm` é quem
deveria assumir a investigação de #2.2/#2.1 e a confirmação do pathway `tcl-xsl`; `@lp-backend-dev`
entra só depois do desenho fechado.

## 5. O que NÃO afirmo (sendo explícito sobre limites)

- Não afirmo que `TargetElementGuid` é ou não estável entre execuções — dado interno do produto
  fechado, não temos como confirmar.
- Não afirmo o shape completo do `MapperVO`/`Rule` real — só o subconjunto que os dois parsers do
  nosso repo efetivamente leem hoje.
- Não afirmo como o `.exe` resolve `I.<Linha>/<Campo>` internamente — não decompilei, não vou
  especular comportamento de binário de terceiro.
- Não afirmo se o pathway `tcl-xsl` usa ou não `MapperVO` — não localizei o código equivalente
  nesta investigação; fica como pergunta em aberto para `@lp-parser-llm`.
