# Specs de execução — raio-X de maturidade (2026-08-16)

Continuação do raio-X de hoje. Cada spec é autocontida — outro agente executa sem
reconstruir contexto. Owner e passos concretos em cada uma.

---

## 1. Alerta ativo de deploy quebrado

**Owner:** `@lp-devops`
**Arquivo:** `.github/workflows/deploy.yml`

A lógica de detecção já existe (linhas ~1075-1224): smoke test de readiness com
retry/backoff em `/health/ready`, rollback automático do backup pré-deploy (PR #129),
e um resumo já escrito em `$GITHUB_STEP_SUMMARY` (bloco em torno da linha 1211-1224,
variável `$resumoLinhas`). Falta só o **disparo de notificação ativa** — hoje o
resumo só aparece pra quem abre a aba Actions manualmente.

**Onde adicionar:** um novo step logo após o bloco de rollback (após a linha ~1224,
dentro do mesmo `if` que já monta `$resumoLinhas`), condicionado a `if: failure()`
ou a uma flag de step-output (`smoke_failed=true`) setada pelo bloco existente.

**Quando dispara** (qualquer um dos três, mutuamente exclusivos):
1. Smoke test de readiness falhou e rollback foi executado com sucesso.
2. Smoke test falhou e rollback também falhou ou não foi possível (caso mais crítico).
3. (Opcional, menor prioridade) Deploy abortado antes do smoke test — falha de build/publish.

**Conteúdo mínimo da mensagem** (reaproveitar `$resumoLinhas`, já formatado):
- Status: `ROLLBACK OK` / `ROLLBACK FALHOU` / `DEPLOY ABORTADO`.
- URL testada e resultado (`$smokeUrl`, `$ultimo`, tentativas).
- Link direto pro run do Actions (`$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID`).
- Commit/PR que disparou o deploy (`github.sha`, `github.ref`).

**Mecanismo — sem infra nova:**
- **Opção A (preferida se a NDD já usa Teams/Slack):** webhook de incoming connector,
  via `curl`/`Invoke-RestMethod` direto no PowerShell do step (sem Action de terceiro
  — evita dependência extra). Precisa de **confirmação do dono**: qual canal e a URL
  do webhook (não inventar/hardcodar URL nenhuma — só ler de secret, ex.: `TEAMS_WEBHOOK_URL`).
- **Opção B (fallback, sem depender de canal corporativo):** e-mail via
  `dawidd6/action-send-mail@v3` (gratuito, Action de terceiro amplamente usada) usando
  SMTP já disponível (ex.: Office 365 da NDD) — precisa de secrets `MAIL_USERNAME`/
  `MAIL_PASSWORD`/`MAIL_SERVER` e do endereço de destino, também a confirmar com o dono.

**Decisão pendente do dono antes de implementar:** qual canal (Teams/Slack/e-mail) e
credenciais associadas. Sem isso não há o que implementar além do step condicional
vazio.

---

## 3a. `merge-gate.yml` não bloqueia de verdade

**Owner:** registro apenas — sem ação de implementação hoje.

Já mapeado em `.claude/agent-memory/lp-devops/github-protections-pending.md`: os
repos da org viraram privados em 2026-08-12, e branch protection nativa (PR
obrigatória, required status checks) é recurso pago no plano free do GitHub
(`403 Upgrade to GitHub Pro`). O dono decidiu não assinar por ora. `merge-gate.yml`
(`verify-source`) continua rodando e falhando visualmente em PRs contra `master`,
mas não bloqueia merge sem a proteção nativa como `required_status_check`.

**Checklist exato para religar, se algum dia a NDD assinar GitHub Pro/Team:**
1. Reaplicar `required_pull_request_reviews` (`required_approving_review_count: 1`,
   `dismiss_stale_reviews: true`) em `master` e `develop` via
   `gh api -X PUT .../branches/{branch}/protection`.
2. Reaplicar `allow_force_pushes: false`, `allow_deletions: false`.
3. Anexar o check `verify-source` (job já existe, nome confirmado rodando no PR #29)
   como `required_status_checks` na proteção de `master`.
4. Confirmar com `gh api repos/LayoutParser/LayoutParserApi/branches/master/protection`
   que `required_status_checks` não é mais `null`.

Detalhe completo e histórico: `.claude/agent-memory/lp-devops/github-protections-pending.md`
(não duplicar aqui, só sincronizar se o estado mudar).

---

## 3b. Rollback automático só cobre smoke de readiness, não pathway funcional

**Owner:** `@lp-devops` (workflow) + `@lp-qa` (define o que conta como "canário saudável")
**Arquivo:** `.github/workflows/deploy.yml`, mesmo bloco de smoke test (linhas ~1075-1224)

Hoje "saudável" = `/health/ready` retorna 200. Isso confirma que a API subiu e as
dependências básicas respondem, mas não confirma que o pathway de transformação
funciona (LowCode runner, mapper resolution, etc. — ver achado de produção
`lowcode-allowedpackageguids-empty-in-null-2026-08-15.md`: a API pode responder
`/health/ready` 200 com o LowCode completamente quebrado).

**Proposta:** adicionar, no mesmo step de smoke test (após o retry de readiness
passar, antes de declarar sucesso), uma chamada real a `execute-candidates` contra
um layout/mapper de teste conhecido — candidato:
`LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c` / `MAP_f31a6758...`. Só declarar o deploy
"verdadeiramente saudável" se essa transformação retornar sucesso; caso contrário,
acionar o mesmo caminho de rollback já existente.

**Trade-off a resolver antes de implementar (decisão de `@lp-qa` + dono):**
- Chamar dado real do banco em produção a cada deploy: adiciona lentidão (mais uma
  chamada HTTP + parsing real) e cria uma dependência permanente de que esse
  layout/mapper específico continue existindo e válido no banco de produção —
  se alguém deletar/alterar esse registro, o canário quebra e passa a bloquear
  deploys legítimos por um motivo não relacionado ao deploy.
- Alternativa mais segura: um **canário sintético** — payload de teste fixo, não
  dependente de dado real do banco, versionado junto do próprio workflow ou como
  fixture no repo. Mais barato de manter, sem acoplamento a dado de produção, mas
  cobre menos superfície real (não valida resolução de mapper real do catálogo).

**Recomendação:** começar pelo canário sintético (menor risco, decidível sem
esperar infra nova); revisitar o dado real como segunda fase se `@lp-qa` avaliar
que o sintético não pega os bugs que importam (ex.: o próprio caso do
`AllowedPackageGuids` vazio, que só se manifesta com resolução real de catálogo).

---

## 5. Login SQL próprio do projeto (não compartilhado)

**Owner:** dono do projeto → escalar ao DBA. Não implementável por agente.

Texto pronto para o dono levar direto à conversa com a DBA/infra:

> **Assunto: solicitação de login SQL dedicado para o LayoutParserApi**
>
> **Contexto:** o LayoutParserApi hoje usa uma credencial de SQL Server
> compartilhada por toda a base ConnectUS (~230 mil times/times de usuários). Essa
> credencial já foi exposta em texto plano no histórico do repositório (commit
> público, ver `LayoutParserApi/.claude/rules/security.md`) e a rotação está
> bloqueada precisamente porque é compartilhada: trocar a senha teria efeito em
> todos os consumidores dessa credencial, não só nesta API — o blast radius de
> qualquer vazamento futuro é permanente, porque rotacionar nunca é uma opção real.
>
> **Pedido:** um login SQL dedicado ao `LayoutParserApi`, com escopo mínimo — acesso
> apenas ao banco `ConnectUS_Macgyver`, e apenas com as permissões que a API
> efetivamente usa hoje (leitura/escrita nas tabelas que o serviço já acessa em
> runtime; sem permissões administrativas ou de outros bancos).
>
> **Ganho concreto:** com um login isolado, se essa credencial vazar no futuro a
> rotação volta a ser uma ação simples e local — trocar a senha desse login não
> afeta nenhum outro sistema, sem precisar coordenar com o resto da NDD. Hoje isso
> não é possível.

---

*Registrado por `@lp-architect` em 2026-08-16, a pedido do dono. Commit local — push
fica com `@lp-devops`.*
