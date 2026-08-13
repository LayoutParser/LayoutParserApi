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

## DECISÃO FINAL (2026-08-12, Aria, review-arch, issue #41)

**Decisão: (A) remover o Pathway 1 agora.** Não é mais "candidato a deprecação" — confirmado sem
consumidor real e sem capacidade exclusiva. Evidência coletada nesta sessão (grep atual, não a de
2026-07-21):

- **Front-end (`LayoutParserReact/`, incluindo `server/` BFF):** zero ocorrências de
  `TransformationController`, `/api/transformation/transform` ou `/api/transformation/available-targets`.
  Todo o código de produção (`src/services/api/transformationService.ts`, `useTransformationStore.ts`,
  `XmlTransformationDisplay.tsx`) chama exclusivamente `/api/transformationexecution/*` (Pathway 2).
- **MCP Server (`mcp/LayoutParserMcp/`):** a única ocorrência é texto de exemplo (`Description`) num tool
  genérico de "chamar qualquer endpoint" (`ApiTools.cs:87`) — não é uma chamada real, nunca foi.
- **CI/scripts (`.github/`):** zero ocorrências de `/api/transformation/transform`.
- **Capacidade exclusiva do Pathway 1?** Não. O único endpoint sem equivalente direto no Pathway 2 é
  `GET /api/transformation/available-targets/{inputLayoutGuid}` (descoberta de layouts de destino a partir
  de um GUID de entrada, varrendo `ICachedMapperService`) — mas ele também **não tem nenhum consumidor**
  (grep confirmado no front-end: zero ocorrências de `available-targets`). Não é uma capacidade em uso que
  precise ser portada antes da remoção; se um dia for necessária, é uma feature nova a desenhar sob demanda,
  não uma migração.
- **XSD validation inline no controller:** é o único pedaço "valioso" do Pathway 1 (chama
  `XsdValidationService.ValidateXmlAgainstXsdAsync` após `TransformAsync`), mas (a) já está arquiteturalmente
  errado — lógica de negócio no controller, contra `dotnet-standards.md` — e (b) o loop de diagnóstico
  XSD→Ollama já foi decidido para entrar em Pathway 2/`TransformationPipelineService` (ver acima, decisão de
  2026-07-21). Não há nada aqui que precise sobreviver à remoção.

**Trade-off considerado e descartado:** manter o Pathway 1 "por precaução" (opção C, deprecar sem remover)
teria sentido se houvesse qualquer sinal de consumidor externo desconhecido (parceiro, script manual,
Postman collection versionada) — não há evidência disso, e manter código morto com `IMapperTransformationService`/
`MapperTransformationService` vivo custa: (1) superfície de ataque sem [Authorize] nem [ServiceFilter(AuditActionFilter)]
(diferente do Pathway 2, que já tem `[ServiceFilter(typeof(AuditActionFilter))]` no controller e `[Authorize(Roles = "admin")]`
nos endpoints privilegiados — ver `TransformationExecutionController.cs`), (2) confusão contínua de manutenção
(qual pathway recebe a próxima feature), (3) DI carregado sem necessidade.

**Plano de execução (para `@lp-backend-dev`, não implementado aqui):**

1. Remover `Controllers/TransformationController.cs` inteiro (inclui `TransformRequest` DTO local).
2. Remover `Services/Transformation/MapperTransformationService.cs` e
   `Services/Transformation/Interface/IMapperTransformationService.cs` — checar antes se `IMapperTransformationService`
   é usado por mais alguém além do controller removido (`grep -r "IMapperTransformationService" --include=*.cs`);
   se não houver outro consumidor, remover os dois arquivos.
3. Remover o registro de DI de `IMapperTransformationService`/`MapperTransformationService` em `Program.cs`
   (grupo Transformation).
4. Checar se `ICachedMapperService.GetMappersByInputLayoutGuidAsync` (usado só pelo `available-targets`
   removido) tem outro consumidor antes de tocar nela — provavelmente fica, é método genérico do cache, não
   exclusivo do Pathway 1.
5. Rodar `dotnet build` — deve compilar sem erros após a remoção (nenhum outro arquivo referencia
   `TransformationController`/`MapperTransformationService` conforme grep desta sessão).
6. Rodar `dotnet test` (`Services/Testing` e projeto de testes, se existir teste cobrindo esse controller —
   não encontrado nesta sessão, mas confirmar).
7. Atualizar Swagger/XML docs se houver referência a `/api/transformation/transform` ou `available-targets`
   (delegar a `@lp-doc` se necessário).
8. Confirmar com `@lp-qa` que nenhum quality gate/smoke test aponta para as rotas removidas.

**Critério de rollback:** se após a remoção surgir um consumidor real esquecido (ex.: script de operação
não versionado no repo), o pathway pode ser recriado a partir do histórico git — não é uma decisão
irreversível, mas a expectativa é que não haja necessidade.
