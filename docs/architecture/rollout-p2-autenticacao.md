# Rollout do P2 — ligar a autenticação sem derrubar o front

> `@lp-architect` (Aria), 2026-08-11. O código já existe: `ApiKeyGateFilter` (global, **fail-open**),
> `ApiKeyGatePolicy` (allowlist por segmento), `Security:ApiKey` / `Security:AnonymousPaths`. P2 **não é
> escrever o gate** — é ligá-lo numa ordem que não tranca o `LayoutParserReact`. A ordem errada dá
> **401 na aplicação inteira**.

## O fato que define a ordem

Verificado hoje: **o front NÃO manda `X-Api-Key`.** O interceptor axios (`api.ts:59-66`) só injeta
`X-Correlation-ID`. Então, no minuto em que `Security__ApiKey` for provisionada em produção, **toda
chamada do React que não estiver na allowlist toma 401** — e nada no front está na allowlist.

Por isso o gate nasce fail-open (documentado no próprio filtro): ligar fail-closed no primeiro deploy
"subiria a API segura e inútil". A ativação é uma sequência, não um commit.

## Sequência seseg — cada passo é inerte até o seguinte

| # | Passo | Repo/Dono | Por que é seguro nesta ordem |
|---|---|---|---|
| 1 | Front passa a **enviar** `X-Api-Key` **se** `VITE_API_KEY` estiver definida | React / `@lp-front-dev` | Gate ainda fail-open; header enviado é **ignorado**. Sem `VITE_API_KEY`, não envia nada. Zero efeito hoje, pré-requisito de tudo |
| 2 | Definir `Security:AnonymousPaths` = `["/health"]` (cobre `/health` e `/health/ready`) | API config / `@lp-devops` | O smoke test do deploy **precisa** alcançar `/health/ready` sem chave. Sem isso, ligar a chave quebra o próprio gate de deploy |
| 3 | Provisionar em **DEV**: `Security__ApiKey` no Environment do serviço + secret `API_KEY_DEV` no CI + `VITE_API_KEY` no build de dev do front | `@lp-devops` + `@lp-front-dev` | Valida ponta a ponta num ambiente descartável: front funciona, smoke test 200, 401 só sem header |
| 4 | Só então **PROD**: `Security__ApiKey` no Environment de produção + `API_KEY_PROD` + build de prod do front com a chave | `@lp-devops`, **aprovação do dono** | É mudança de segurança em produção. Passa pelo environment reviewer (quando ligado) e pela decisão do dono. Nunca antes de o passo 3 fechar verde |

**Regra de ouro:** a chave de produção só entra **depois** de o front de produção já estar enviando o
header e o dev ter validado. Inverter isso é o 401-geral.

## Duas ressalvas honestas — decisão do dono, não detalhe

1. **Sem TLS, a chave viaja em claro.** `UseHttpsRedirection` está comentado (`Program.cs`). Chave
   compartilhada sobre HTTP é *"sensação de segurança, não segurança"* (palavra do próprio filtro).
   Numa rede interna fechada pode ser um interino aceitável — mas é **decisão consciente**, não algo a
   silenciar. Recomendo TLS **junto ou antes** de habilitar em prod, ou registrar o risco aceito.
2. **Chave compartilhada é um degrau, não o destino.** Não identifica quem chamou nem permite revogar
   um consumidor. O alvo recomendado é **autenticação integrada Windows/Negotiate** — o ambiente é AD,
   então já suporta. P2 fecha o buraco de "API 100% anônima"; não é a arquitetura final de auth.

## ⛔ CORREÇÃO ESTRUTURAL (Aria, 2026-08-11, após o passo 1) — a premissa de 2 camadas estava errada

Escrevi esta spec assumindo **browser → API .NET** (2 camadas). **A realidade é 3 camadas**, e há um
esforço de autenticação paralelo que eu não tinha visto:

```
Browser  ──(Entra OIDC, sessão cifrada)──►  BFF Fastify (server/)  ──(proxy /api)──►  API .NET
```

- O `LayoutParserReact` tem um **BFF Node (Fastify)** em `server/`, e a branch `codex/feat-entra-oidc`
  está implementando **login via Microsoft Entra ID (Azure AD)** nele: `server/src/oidc.ts` (novo),
  `AuthenticationGate.tsx` (novo), session store cifrada, roles vindas de claims do token.
- O BFF já faz proxy de `/api` para o upstream (`app.ts:296`, `config.upstreamUrl`) e, no
  `rewriteProxyHeaders` (`app.ts:115`), **remove `authorization` e `cookie`** (a sessão do usuário
  nunca vaza para a API) e **injeta headers de identidade** (`trustedUserHeader`,
  `trustedRolesHeader`, `x-correlation-id`).

**O que isso quebra na minha spec:**

1. **O passo 1 está na camada errada.** O browser fala com o **BFF**, não com a API. Pôr a chave no
   bundle (`VITE_API_KEY`) **expõe o segredo compartilhado a quem abrir a SPA** e é redundante — a
   confiança do browser é a sessão Entra, não uma API key. A entrega do Remy está **correta como
   código e verde nos testes**, mas é **o design errado para esta arquitetura**. Fica **retida, não
   mergeada** (o Remy já a segurou por instinto certo — a árvore está na branch do OIDC).

2. **O contrato do salto BFF→API não está acordado.** Hoje o BFF manda `trustedUserHeader`/roles; a
   API (com o gate ligado) espera `X-Api-Key`. **Nenhum dos dois fala a língua do outro.** O
   `ApiKeyGateFilter` **tem lugar sim** — mas o `X-Api-Key` é injetado pelo **BFF** no
   `rewriteProxyHeaders`, server-side, **nunca pelo browser**.

### Arquitetura reconciliada (recomendação, decisão do dono)

Os dois esforços são **complementares, em camadas diferentes** — não concorrentes:

| Fronteira | Mecanismo | Responde |
|---|---|---|
| Browser ↔ BFF | **Entra OIDC** (branch `codex/feat-entra-oidc`) | *quem é o usuário* — identidade real, roles, revogação, sem segredo no browser |
| BFF ↔ API .NET | **rede** (a API só aceita conexão do BFF) **ou** `X-Api-Key` injetado pelo BFF | *esta chamada vem do nosso BFF confiável* |
| Dentro da API | consumir `trustedUserHeader`/roles para autorização e auditoria | *o quê este usuário pode fazer* — mecanismo que a API **ainda não tem** |

Entra OIDC **é o "destino recomendado"** que esta própria spec nomeou (auth integrada AD). A chave
compartilhada deixa de ser a defesa principal e vira, no máximo, o cinto do salto BFF→API.

### O que fica PARADO até o dono decidir a fronteira BFF↔API

- **Não** mergear o passo 1 do Remy como está (chave no browser).
- **Não** provisionar `Security__ApiKey` para o browser usar.
- A pergunta para o dono: o salto BFF→API é protegido por **rede** (mais simples, a API só escuta o
  BFF) ou por **`X-Api-Key` injetado no BFF** (defesa em profundidade, mas mais uma chave para girar)?
  E: a API deve passar a **consumir a identidade** (`trustedUserHeader`) para autorização por papel,
  ou o BFF já basta como porteiro nesta fase?

Até isso, o P2 "chave compartilhada" está **suspenso** — não porque falhou, mas porque a arquitetura
real é melhor e já está sendo construída noutra frente.

---

## (Histórico) O que eu havia disparado — antes da correção acima

**Só o passo 1** (front enviar o header condicionalmente). Entregue e verde, mas **retido** pela
correção estrutural acima — era o design de 2 camadas.
