---
name: adr-m2m-cypress-opcao-a-2026-09-03
description: ADR M2M/Cypress revisado de Opcao B para Opcao A (client credentials Entra) a pedido do dono, com secao nova de honeypot/canary
metadata:
  type: project
---

O ADR `docs/architecture/adr-autenticacao-m2m-e2e-cypress-2026-09-03.md` (issues #218/#221) foi
revisado no mesmo dia: veredito trocado de Opção B (identidade sintética, só fora de produção)
para **Opção A (OAuth2 client credentials via Entra)**, a pedido explícito do dono.

**Por quê:** o dono declarou que o Cypress "uma hora vai precisar fazer os testes end-to-end
direto na API" de forma recorrente, não como gate pontual — exatamente a condição de gatilho que
a recomendação original de B já citava como sua própria expiração. Pagar o custo de A agora evita
pagar o custo de implementação duas vezes (B agora, migrar para A depois).

**Decisões de mecanismo registradas no ADR:**
- Novo `AuthenticationScheme` JWT Bearer paralelo ao `TrustedIdentityMiddleware` — não extensão
  dele. Motivo: são dois modelos de confiança diferentes (rede/loopback vs. criptografia/token).
- App Role mínima `Service.E2E` no Entra, mapeada para a mesma role interna `servico-e2e` que a
  Opção B já havia desenhado — nunca `admin`.
- `client_secret` nunca na API/`appsettings.json` — só do lado Cypress/CI. A API só valida token
  (Authority/Audience são config pública, podem ir no `appsettings.json`).
- Trade-off de rede (127.0.0.1) tratado com 3 caminhos: co-localização/túnel do runner
  (preferido, não reabre a trava de rede), exceção de IP pontual (custo declarado, decisão do
  dono), abrir tudo (descartado). Pendência: `@lp-devops` confirmar topologia real do runner do
  Cypress antes da implementação.

**Nova capacidade desenhada — honeypot/canary tokens** (pedido também do dono, camada
complementar, não substitui auth real):
- Endpoint-isca (ex. `execute-legacy`, nome plausível dado o padrão do projeto) que não faz nada
  real, só loga `Critical` em qualquer hit.
- Credencial-isca (API key "aposentada") reconhecida só para detecção, nunca autentica.
- Alerta via log `Critical` correlacionável + sink de e-mail condicional no Serilog, reaproveitando
  os mesmos secrets SMTP já pendentes em `.claude/rules/security.md` (provedor ainda não escolhido
  pelo dono — mesmo bloqueio que já existia pro alerta de deploy).

**How to apply:** se o assunto voltar (implementação por `@lp-backend-dev`, ou nova decisão sobre
a topologia do runner do Cypress), este é o ADR de referência — não recriar do zero. Ver também
[[gemini-openai-decommission-decision]] pelo padrão já usado de "revisar decisão anterior quando o
dono muda a premissa, sem apagar o raciocínio anterior".
