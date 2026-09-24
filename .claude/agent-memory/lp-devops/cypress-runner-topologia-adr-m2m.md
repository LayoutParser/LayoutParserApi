---
name: cypress-runner-topologia-adr-m2m
description: LayoutParserCypress não tem CI/remoto — suíte E2E (execute-lowcode) só roda manual do workstation; runner dev-local já co-localizado com API de dev é candidato natural se CI vier a existir
metadata:
  type: project
---

Investigação de 2026-09-03 para preencher pendência do ADR M2M
(`docs/architecture/adr-autenticacao-m2m-e2e-cypress-2026-09-03.md`, issues #218/#221):
"onde roda o runner do Cypress" para decidir entre co-localização (Opção 1) vs. exceção de
firewall (Opção 2).

**Achado central: não existe runner nenhum hoje.** `LayoutParserCypress` (ver
[[layoutparser-cypress-bootstrap]]) não tem remoto/GitHub nem workflow — só clone local. A suíte
que importa para este ADR (`nfe-emissao-normal.cy.js`, bate em `execute`/`execute-lowcode`/
`generate-for-layout`) só foi rodada manualmente pela QA (Cass), contra `dotnet run` local porta
5000 (ver memória `.claude/agent-memory/lp-qa/cypress-alpha-emissao-normal-spec.md`), nunca contra
a instância de dev implantada nem em CI.

**Não confundir com o Job 2 (`ia-candidates-batch.cy.js`)** — suíte Cypress diferente, já
provisionada e rodando via cron na VM Ubuntu (ver [[runner-isolation-rollout]] e
`docs/architecture/handoff-3-job2-pipeline-cypress-pollux.md`). Essa VM tem rede comprovadamente
quebrada para produção (172.25.32.42) e não chama os endpoints `[Authorize]`-gated deste ADR — não
é candidata a runner para o problema de auth M2M.

**Candidato de menor esforço, se/quando o CI for criado:** o runner self-hosted `dev-local`
(máquina `NDD-NOT-10910`) já é usado por este repo (`ci-dev.yml`) e já roda co-localizado com a
API de dev implantada (`localhost:5100`, bind loopback default — confirmado em `ci-dev.yml:355-357`,
`API_URL_DEV` não setado). Se o futuro workflow do `LayoutParserCypress` reaproveitar esse runner
(ou um runner novo na mesma máquina), Caminho 1 (co-localização, sem reabrir 127.0.0.1) fica
trivial. **Isso não está configurado nem decidido** — é só o caminho disponível de menor atrito.

**Pergunta em aberto, só respondível numa sessão do `LayoutParserCypress` ou pelo dono:**
1. O CI futuro vai usar runner self-hosted na mesma rede/máquina da API de dev, ou GitHub-hosted?
2. A suíte vai rodar contra a instância de dev implantada (5100) ou API local ad hoc?

Registrado como adendo no próprio ADR + comentado nas issues #218/#221. Commit local `25e98be`
na branch `worktree-agent-aaf1b7c089448af06` (arquivo não existia nessa branch antes — o ADR
original vive só na branch `feat/resolucao-estrutural-txt-xml-140`, checked out em outro worktree;
não consegui mesclar diretamente por isolamento de worktree). **Fica pendente decidir com o dono
como reconciliar as duas branches/cópias do ADR antes do próximo push.**

**How to apply:** se `@lp-architect`/dono pedir para avançar a implementação do mecanismo M2M
(client credentials Entra), lembrar que a Parte 1 do plano (auth em si) não depende de resolver
rede — só a Parte de CI do Cypress depende. Não bloquear a implementação da API esperando essa
decisão de topologia.
