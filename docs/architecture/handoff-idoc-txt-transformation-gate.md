# Gate de transformação — IDoc/TXT parseado → XML final

> Decisão de `@lp-architect` para implementação por `@lp-parser-llm` e gate por `@lp-qa`.
> Escopo: `POST /api/parse/upload` e o pathway low-code/Sysmiddle já existente.

## Problema reproduzido

Um documento SAP IDoc em TXT foi corretamente detectado e parseado por quebras de linha:

- `detectedType = idoc`;
- 55 linhas físicas;
- 263 campos parseados em 28 tipos de linha;
- nenhum erro de tamanho de linha.

Mesmo assim, a resposta trouxe `transformationsStatus = not_applicable`. A causa não estava no
parser nem no runner: `ParseController.Upload` só iniciava a transformação quando
`detectedType == "mqseries"`.

O primeiro artefato usado no teste é um `LayoutVO` (`LayoutType=TextPositional` e
`WithBreakLines=true`), não um `MapperVO`. Ele descreve como parsear o IDoc; a transformação final
ainda depende de um mapper real cadastrado no catálogo para o `InputLayoutGuid` desse layout.

## Decisão

O subtipo detectado participa de uma **allowlist explícita**, conforme a
[`spec-fase3-fase4-gate-transformacao-e-dataset.md`](spec-fase3-fase4-gate-transformacao-e-dataset.md).
Depois do retorno antecipado de XML puro, somente `mqseries`, `idoc`, `unknown` e `txt` podem seguir
para a transformação. Uma allowlist é necessária para que tipos futuros, como `edifact` ou `json`,
não entrem acidentalmente em um runner desenhado para texto posicional.

O gate passa quando todos os critérios forem verdadeiros:

1. o parse principal concluiu com sucesso;
2. `detectedType` pertence à allowlist posicional (`mqseries`, `idoc`, `unknown`, `txt`);
3. `layoutGuid` está preenchido;
4. o texto original não está vazio.

Isso cobre:

- MQSeries posicional de largura fixa;
- IDoc/TXT com registros separados por quebra de linha;
- TXT genérico detectado como `unknown`, desde que o layout tenha conseguido parseá-lo.

O conteúdo enviado ao runner continua sendo o texto original, preservando as quebras de linha do
IDoc. A estratégia de divisão (largura fixa versus quebra de linha) pertence ao parser e não deve
ser reimplementada no gate. `PositionalFormat` não substitui a allowlist: ele rotula **como** o texto
foi dividido, enquanto `detectedType` decide se o pathway é aplicável.

## Rastreabilidade do dataset

Abrir o gate faz o IDoc começar a persistir amostras no store low-code. Para não misturar IDoc por
linhas com MQSeries contínuo, todo `meta.json` novo usa `datasetSchemaVersion = 2` e registra no
objeto raiz, tanto no caminho de candidato único quanto no de múltiplos candidatos:

- `positionalFormat`: `RecordPerLine` ou `ContinuousStream`;
- `positionalFormatSource`: `layout`, `heuristic` ou `default`;
- `withBreakLines`: `true`, `false` ou `null`;
- `layoutType`;
- `suspect` e `suspectReason`.

Uma amostra é suspeita quando o formato não veio explicitamente do layout. O pathway da requisição
separada não pode mais hardcodar `detectedType = mqseries`; quando não houver informação suficiente,
deve registrar `unknown`/`default` e marcar a amostra como suspeita.

## Gate da aba XML (requisição separada)

A aba também chama `POST /api/transformation-execution/execute-candidates`. Esse endpoint resolve
o layout novamente pelo nome e, no código anterior, dependia exclusivamente do `LayoutGuid` da
linha do catálogo. Há layouts legados cujo GUID do banco é zero, embora o XML processado contenha
um `LAY_*` válido; nesse caso o parse automático conhece o GUID correto, mas o botão da aba perde
essa informação e elimina o candidato Sysmiddle.

O contrato passa a aceitar `layoutGuid` opcional em `TransformationRequest`:

1. o front envia `parseResult.layout.layoutGuid`;
2. o pathway Sysmiddle prioriza esse valor quando preenchido;
3. na ausência dele, usa o `LayoutGuid` não-zero do catálogo;
4. se ambos estiverem ausentes, mantém zero candidatos com warning — nunca inventa GUID;
5. a consulta por nome continua obrigatória para validar o layout e alimentar o pathway TCL/XSL.

O campo é aditivo: clientes antigos que só enviam `layoutName` continuam funcionando para layouts
cujo catálogo já possui GUID válido.

## Contrato de resposta

O shape existente permanece compatível:

- `completed`: transformação aplicável e concluída dentro do teto síncrono; `transformations`
  contém os candidatos e seus XMLs;
- `processing`: excedeu o teto síncrono, mas continua em background;
- `not_applicable`: o documento passou pelo gate, porém não existe mapper aplicável;
- `error`: falha estrutural da transformação; o parse principal continua com HTTP 200.

Campo aditivo opcional para diagnóstico da UI:

```text
transformationsReason =
  no_mapper | empty_input | timeout_sync | structural_error | type_not_positional
```

`transformationsReason` não substitui `transformationsStatus`; apenas explica o estado. Para XML
puro, o retorno antecipado existente continua sendo usado e o pathway não é chamado.

## Resiliência, performance e segurança

- A transformação nunca derruba nem prolonga o parse além de `LowCode:SyncDeliveryTimeoutSeconds`.
- Todo TXT parseado passa a consultar candidatos por `layoutGuid`; sem mapper, degrada para
  `not_applicable` sem iniciar o processo externo.
- O runner continua limitado pelo semáforo global (`LowCode:MaxConcurrentRunners`).
- Documento real não deve ser logado nem incluído em fixture versionada.
- O caso real não deve ser enviado a LLM em nuvem.
- O teste ponta a ponta só pode declarar XML entregue quando SQL/cache localizar o mapper e o
  runner Sysmiddle estiver disponível. Falha dessas dependências deve aparecer como degradação,
  nunca como falso sucesso do gate.

## Critérios de aceite

1. MQSeries continua elegível.
2. IDoc por linhas é elegível.
3. TXT/`unknown` que parseou é elegível.
4. XML puro, EDIFACT, JSON e qualquer tipo fora da allowlist não são elegíveis.
5. Texto vazio ou parse com falha não é elegível.
6. Ausência de mapper não vira erro do parse.
7. Timeout/falha do runner não vira erro do parse.
8. A aba pode enviar o GUID retornado pelo parse mesmo quando o catálogo possui GUID zero.
9. O `meta.json` v2 separa `RecordPerLine` de `ContinuousStream` e preserva a origem da decisão.
10. Build e testes do `LayoutParserApi.Tests` passam.
