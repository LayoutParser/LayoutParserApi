# Prompt: autenticação machine-to-machine + validação do layout FIAT

> Origem: gate e2e Cypress (repo `LayoutParserCypress`) rodando contra a API real do
> `LayoutParserApi` em ambiente de desenvolvimento detectou dois bloqueadores. Ambos
> foram registrados como issues (#218, #219) e amarrados neste EPIC. Este documento é o
> prompt autocontido para quem for implementar — pode ser colado direto num agente
> (`@lp-architect`, `@lp-backend-dev`, `@lp-parser-llm`) ou usado por um dev humano.

## Contexto

O repo `LayoutParserCypress` mantém uma suíte E2E que valida, ponta-a-ponta, que
documentos TXT transformados pela `LayoutParserApi` (via TCL/XSL gerados para um layout
SysMiddle) são aceitos pelo e-forms/Pollux (SEFAZ fake de desenvolvimento da NDD). Esse
gate roda `cy.request()` diretamente contra os endpoints da API — sem navegador, sem
sessão interativa.

Ao rodar esse gate contra a API real, dois problemas bloquearam a suíte inteira:

1. A API passou a exigir login OAuth (Google/Microsoft) — não existe hoje nenhum
   mecanismo de autenticação de serviço para um cliente automatizado (como o Cypress)
   bater direto nos endpoints sem essa interação.
2. O endpoint `generate-for-layout`, quando chamado para o layout FIAT
   (`LAY_TXT_MQSERIES_ENVNFE_4.00_NFe`, GUID `ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c`),
   recusa a chamada com o erro `"Tipo de layout não suportado: 2"`. Não está confirmado
   se é uma lacuna real no código (o `layoutType=2` deveria ser suportado e não é) ou um
   cadastro incorreto desse layout específico no banco.

Essas duas frentes são independentes uma da outra, mas ambas bloqueiam o mesmo gate —
por isso viraram um EPIC único, com uma issue-filha para cada achado (#218 e #219).

## Frente A — Autenticação machine-to-machine (dono sugerido: `@lp-architect` decide o
mecanismo, `@lp-backend-dev` implementa)

**Não decida a solução técnica sozinho de forma unilateral fora de uma avaliação
explícita** — isto é decisão de arquitetura, não uma escolha implícita de implementação.
Avalie as opções plausíveis e registre a decisão (com um ADR ou doc em
`docs/architecture/`) antes de implementar:

- OAuth2 client credentials grant (service principal, sem usuário humano no fluxo);
- API key de serviço com escopo restrito (ex.: só os endpoints do fluxo de
  transformação, não o CRUD administrativo);
- mTLS (certificado cliente);
- service account interno (usuário técnico "de sistema" com flag especial no banco).

**Requisito não-negociável:** o mecanismo escolhido não pode, em nenhuma etapa, exigir
interação de navegador (redirect para tela de login, popup, MFA interativo). Tem que ser
algo que um `cy.request()` do Cypress consiga completar programaticamente — request para
obter/apresentar a credencial, sem qualquer passo manual.

**Critério de aceite:** com a credencial de serviço, uma chamada legítima aos seguintes
endpoints retorna 200 (fluxo completo do gate):

- `POST /api/AutoTransformation/generate-for-layout`
- `POST /api/transformation-execution/execute`
- `POST /api/TransformationExecution/execute-lowcode`

## Frente B — `generate-for-layout` recusa `layoutType=2` para o layout FIAT (dono
sugerido: `@lp-parser-llm` investiga)

Investigar por que o layout FIAT `LAY_TXT_MQSERIES_ENVNFE_4.00_NFe`
(`ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c`) é recusado por `generate-for-layout` com
`"Tipo de layout não suportado: 2"`.

**Critério de aceite — uma das duas conclusões, documentada:**

- (i) `layoutType=2` deveria ser suportado pelo endpoint e há uma lacuna real de código
  — corrigir a lacuna; ou
- (ii) o layout está cadastrado com o tipo errado no banco — corrigir o cadastro.

Documentar explicitamente qual dos dois casos era, com evidência (trecho de código ou
registro do cadastro).

## Ao concluir

Quando as duas frentes estiverem resolvidas (ou parcialmente, avise qual), sinalize o
repo `LayoutParserCypress` (`@cy-devops` ou `@qa-cypress`) para reexecutar o gate e2e
(`npm run test:mappers`) e fechar as issues correspondentes lá (#9 e #10 nesse repo).

## Rastreamento

- EPIC: (ver issue no board `LayoutParserApi`, título `epic: autenticação
  machine-to-machine + validação do layout FIAT para desbloquear e2e Cypress`)
- Issue-filha Frente A: #218
- Issue-filha Frente B: #219
