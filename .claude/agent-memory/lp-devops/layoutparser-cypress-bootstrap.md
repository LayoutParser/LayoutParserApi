---
name: layoutparser-cypress-bootstrap
description: Novo repo LayoutParserCypress criado do zero (2026-07-28) para testes E2E de aceitação de NF-e no e-forms/Pollux
metadata:
  type: project
---

Criei `C:\Users\elson.lopes\source\repos\LayoutParserCypress` em 2026-07-28 — 5º repo do
ecossistema LayoutParser, dedicado a testes E2E (Cypress) validando que as transformações de
NF-e da `LayoutParserApi` (pathways **Sysmiddle/LowCode-auto** `POST /api/parse/upload` e
**TCL/XSL/Canônico** `TransformationExecutionController`) são aceitas pelo ambiente
**e-forms/Pollux** (SEFAZ fake de dev da NDD). Objetivo de fundo: usar a aceitação/rejeição
como oráculo empírico para desambiguar mapeadores (Fiat tem múltiplas linhas em `tbMapper`
pro mesmo `InputLayoutGuid`).

**Decisão explícita do usuário: NÃO vendorizar** nada de
`C:\Users\elson.lopes\source\repos\ndd-api-plataforma-cypress` (specs, fixtures, support) —
aquele repo é da plataforma NDD/API Central (produto diferente), serviu só de inspiração
conceitual (Cypress + Pollux). Todo o scaffold foi escrito do zero.

**Estado do bootstrap:**
- Git local: `master`, commit inicial `4eaa048` ("chore: scaffold inicial do LayoutParserCypress").
  Sem push, sem remoto GitHub criado — autorização de push é sempre do usuário
  (ver [[env-gh-cli-ausente]], gh CLI também não disponível nesta workstation).
- Cypress `^15.19.0` instalado via npm (não pnpm/yarn). `cypress.config.js` com `baseUrl`
  placeholder (`http://localhost:5000`, claramente de exemplo).
- `cypress.env.json` (gitignored, confirmado com `git status --short --ignored=matching` →
  aparece como `!!`) foi **preenchido em 2026-07-28** com valores reais extraídos de
  `ndd-api-plataforma-cypress/ndd-eforms-stage` (URL real do e-forms WebServices, CNPJ/IE de
  teste, credencial SQL do connector) e da própria `LayoutParserApi` (`launchSettings.json`,
  porta 5214 local). Credenciais de API Central (`ndd-api-central-cypress`) foram
  **deliberadamente excluídas** — produto diferente, fora do escopo do alpha (só e-forms/Pollux).
  `poluxUsername`/`poluxPassword` do `.example` ficaram **vazios** — o fluxo SOAP real
  inspecionado não usa Basic Auth/Authorization header; se um fluxo de auth separado existir,
  precisa ser preenchido manualmente depois. Detalhe (nomes de chave, não valores) só na
  transcript da sessão que fez o preenchimento — não duplicado aqui por ser credencial real.
- Único agente do repo: `@qa-cypress` (persona Cass), definido em
  `.claude/agents/qa-cypress.md` — regras próprias (nunca inventar URL/credencial, nunca
  declarar teste verde sem rodar de verdade).

**Escopo alpha (não expandir sem pedido explícito do usuário):** só emissão normal de NF-e,
comparando os dois pathways. Cancelamento e Inutilização ficam de fora — Inutilização em
particular precisa encadear com uma rejeição real (enviar → capturar nNF/série rejeitada →
inutilizar esse número), não é extensão trivial de spec.

**Como aplicar:** se o usuário pedir trabalho neste repo novo (specs, CI, etc.), este é o
ponto de partida — não presumir que specs/fixtures já existem além do que está listado aqui;
confirmar estado atual com `git log`/`ls` antes de assumir.
