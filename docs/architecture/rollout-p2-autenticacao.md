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

## O que estou disparando agora

**Só o passo 1** (front enviar o header condicionalmente). É o único inerte hoje, é pré-requisito de
todos os outros, e é reversível. Os passos 2–4 são config/rollout do `@lp-devops`, e o passo 4 é
**decisão do dono** — não serão executados sem validação de dev e aval explícito.
