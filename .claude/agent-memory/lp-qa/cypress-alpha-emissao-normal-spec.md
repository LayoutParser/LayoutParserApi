---
name: cypress-alpha-emissao-normal-spec
description: Spec Cypress de emissão normal de NF-e (TCL/XSL + LowCode vs Pollux) escrita em LayoutParserCypress; ambos pathways bloqueados localmente por dependência de arquivos só presentes no servidor de produção.
metadata:
  type: project
---

Spec `cypress/e2e/nfe-emissao-normal.cy.js` criada em `LayoutParserCypress` (2026-07-29), cobrindo
os dois pathways de transformação contra o oráculo real e-forms/Pollux (`enviarNFeParaPolux` em
`cypress/support/commands.js`, adaptado do `inserirDocumento.cy.js` real de
`ndd-api-plataforma-cypress/ndd-eforms-stage`). Entrada real: TXT MQSeries de exemplo
(`.claude/tmp/exemplos/txt input/QMWNFe1_...mq_series.txt` do LayoutParserApi, copiado para
`cypress/fixtures/txt-input/`).

Confirmado nesta sessão (rodando a LayoutParserApi local de verdade, `dotnet run`, porta 5000):

- Rota real é `api/TransformationExecution/execute` (PascalCase, sem kebab-case — não existe
  `RouteTokenTransformerConvention` no `Program.cs`). `TransformationRequest` exige `SourceDocumentType`
  e `ExpectedOutput` no body mesmo vazios (nullable reference types + `[ApiController]` = required
  implícito) — sem eles o `/execute` retorna 400 antes mesmo de tentar transformar.
- **Pathway TCL/XSL bloqueado no dev workstation**: `TransformationPipelineService` busca o arquivo
  `MAP_{layoutName}.xml` em `TransformationPipeline:MappingPath` (default
  `C:\inetpub\wwwroot\layoutparser\Mapeamentro`) — esse diretório **não existe** localmente, nem no
  dump do servidor em `.claude/tmp/servidor/layoutparser/` (lá só tem `.tcl`/`.xsl`, não o `.xml` de
  MAP no formato/nome esperado). Testado com `LayoutName=LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe` (GUID
  real `e339073e-32d1-492e-ae8a-dcf6337b21a1`, confirmado via cache Redis) → erro
  `"Arquivo MAP não encontrado para layout: ..."`.
- **Pathway LowCode também bloqueado no dev workstation**: `/execute-lowcode` tenta iniciar
  `C:\inetpub\wwwroot\layoutparser\api\LayoutParserLowCodeRunner.exe`, que também só existe no
  servidor de produção → 500 `"The system cannot find the file specified"`. Mapper resolvido com
  sucesso via `GET /api/mapperdatabase/by-input/{layoutGuid}` (funciona com cache Redis mesmo com SQL
  fora do ar — `MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE`), então o bloqueio é só o `.exe`, não o
  catálogo.
- **Ambiguidade de layout confirmada como esperado**: existem pelo menos 5 variantes de layout
  MQSeries/ENVNFE muito parecidas no catálogo (`LAY_TXT_MQSERIES_ENVNFE_4.00_NFe`,
  `LAY_CNHI_TXT_...`, `LAY_TXT_COMAU_...`, `LAY_IVECCO_...`, `LAY_MARELLI_...`) — todas com o mesmo
  formato de linha de 600 chars, mas mapeadores diferentes. Reforça a motivação original do repo
  Cypress (ambiguidade Fiat/tbMapper). Não dá pra assumir qual variante é a "correta" pro TXT de
  exemplo sem rodar contra o oráculo Pollux de verdade — é exatamente o que a spec resolve, uma vez
  destravado o ambiente.
- **Auth do Pollux confirmada dispensável**: `poluxUsername`/`poluxPassword` vazios em
  `cypress.env.json` está correto — o SOAP real (`WSInserirDocumento`/`WSConsultarProtocolo`) não usa
  header de autenticação, confirmado lendo `inserirDocumento.cy.js` de referência.
- A API sobe e degrada corretamente sem SQL (usa cache Redis pré-populado) — bom sinal de
  resiliência, mas significa que testar contra dev local depende inteiramente do cache já ter sido
  aquecido antes (não valida mudanças recentes de catálogo se o SQL real estiver fora do ar).

**Não rodado ainda ponta-a-ponta contra o Pollux real** — bloqueado antes de chegar lá pelos dois
itens acima. Precisa de `@lp-devops`/`@lp-backend-dev` pra replicar (ou apontar via config) os
arquivos MAP/.exe do runner no ambiente de dev, ou rodar a spec a partir de uma máquina que já tenha
o ambiente completo (o servidor de produção/stage, não este workstation).

**Como apoio a este achado:** `docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md`
(Gap 1) e memória [[lowcode-auto-multicandidate-qa-gate]] já mapeiam o candidato LowCode; ver também
memória `sysmiddle-runtime-e-sintese` (harness MEMORY.md do usuário) sobre o runner Sysmiddle
in-process também bloqueado por licença do host FiatMQ — mesma classe de bloqueio de ambiente, dessa
vez pelo lado do `.exe` externo em vez da licença.
