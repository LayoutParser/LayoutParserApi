---
name: transformation-pathway-duplication
description: Duas pipelines de transformação paralelas e desconectadas na API — só uma tem validação XSD, só a outra é chamada pelo front-end
metadata:
  type: project
---

A API tem **dois caminhos paralelos** para "TXT/XML → XML final", que não se chamam entre si:

- **Pathway 1** — `Controllers/TransformationController.cs` (`POST /api/transformation/transform`) →
  `MapperTransformationService.TransformAsync` (busca Mapper por `InputLayoutGuid`+`TargetLayoutGuid`,
  gera/carrega TCL+XSL). O *controller* (não o service) chama `XsdValidationService.ValidateXmlAgainstXsdAsync`
  depois do `TransformAsync` e devolve `xsdValidation` no JSON. **Confirmado (grep em
  `LayoutParserReact/src/`): zero chamadas do front-end a este endpoint** — parece órfão do lado do front,
  embora eu não tenha checado todos os consumidores possíveis fora do front (MCP, outras tools).
- **Pathway 2** — `Controllers/TransformationExecutionController.cs` (`POST /api/transformationexecution/execute`)
  → `TransformationPipelineService.TransformTxtToXmlAsync`/`TransformXmlToXmlAsync` (busca por `LayoutName`+
  `SourceDocumentType`/`TargetDocumentType`, strings, não GUID) → opcionalmente
  `TransformationValidatorService.ValidateTransformationAsync` (comparação com `ExpectedOutput`/TclPath/XslPath —
  parece voltado a teste/QA, não validação de schema SEFAZ). **Grep em `TransformationPipelineService.cs`: zero
  menções a "Xsd" — este pathway não valida contra XSD hoje.** É o pathway que o front-end **já chama de fato**
  (`transformationService.executeTransformation` → `/api/transformationexecution/execute`), usado por
  `XmlTransformationDisplay.tsx` (ver [[frontend-transformation-tab-built]]).

**Why importa:** em 2026-07-21 recebi um pedido do dono do projeto para desenhar um loop de diagnóstico XSD→Ollama
"em cima do endpoint de transformação existente". O plano proposto (conectar `XsdValidationService` ao loop de
`MapperTransformationService`) mira o **Pathway 1 — que o front-end não chama**. Sem reconciliar isso antes,
qualquer trabalho de backend nos itens de validação/diagnóstico fica invisível para a UI já construída, que
usa o Pathway 2.

**Decisão fechada em 2026-07-21 (Aria, passo 0):** checagem do MCP feita — `mcp/LayoutParserMcp/Tools/ApiTools.cs`
só cita `/api/Transformation/generate` como texto de exemplo num tool genérico de "chamar qualquer endpoint",
não é uma chamada real. **Pathway 2 é o canônico** a partir de agora para validação XSD + o novo loop de
diagnóstico Ollama. Pathway 1 (`TransformationController`/`MapperTransformationService`) fica sem novo
investimento — candidato a deprecação/remoção, decisão final de remover é do dono do projeto/`@lp-devops`,
não decidida aqui.

**Terceiro caminho, descoberto em 2026-08-03 (o mais enganoso):** o `ParseController` também dispara
transformação low-code embutida no próprio parse (`LowCodeAutoTransformationService`) e devolve um
array `transformations` no payload. **O front NUNCA lê esse array** — `ParseResponse` em
`LayoutParserReact/src/types/api.ts` sequer declara o campo; só `transformationsStatus` é usado, e
apenas para rótulo de aba e banner de erro. O XML que aparece na aba vem de um botão que chama
`execute-candidates` (Pathway 2) sob demanda. Consequência prática: **mexer no gate/pathway do
`ParseController` não muda nada na tela** — muda o payload e o dataset de aprendizado. Não prometa
efeito visual a partir dali sem trabalho de front junto.

Ainda no mesmo achado: quando o low-code estoura o teto síncrono, o resultado vai para
`ML:LowCodeTransformationsPath`, que é **write-only** — nenhum controller lê o store, e não há
polling/SSE no front. `transformationsStatus='processing'` nunca resolve.

**How to apply:** ao implementar a validação XSD em Pathway 2, colocá-la dentro de
`TransformationPipelineService`/`TransformationValidatorService` (camada de serviço), **não** replicar o
padrão do Pathway 1 de chamar `XsdValidationService` direto do controller — isso já viola a regra do projeto
("não coloque lógica de negócio no controller", `dotnet-standards.md`). O Pathway 1 é o exemplo do que não
copiar, mesmo tendo chegado à validação primeiro.
