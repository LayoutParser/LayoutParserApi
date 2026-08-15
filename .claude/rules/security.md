---
description: Regras de segurança e a pendência crítica de segredos versionados.
---

# Segurança — LayoutParser API

## Segredos versionados — status da remediação

Os segredos estavam em texto plano no [`appsettings.json`](../../appsettings.json) **e** em fallbacks
hardcoded no código (`GeminiAIService`, `LayoutDatabaseService`, `ElasticSearchLogger`).

| Segredo | Onde | Status |
|---------|------|--------|
| API key do **Gemini** | `Gemini:ApiKey` | Removido do código/JSON ✅ · **Gemini decomissionado (2026-07-21) — revogar/desprovisionar, não rotacionar** 🔴 |
| Senha do **SQL Server** | `Database:Password` | **REGRESSÃO em 2026-07-18** (ver abaixo) · removido de novo ✅ · repositórios da org PÚBLICOS desde 2026-08-15 · **ROTAÇÃO NÃO É OPÇÃO (ver 2026-08-15 abaixo) — mitigação é limpeza de histórico + hardening em repouso + prevenção de reincidência** 🔴🔴 |
| Credenciais do **Elastic** | `ElasticSearch:Username/Password` | ✅ Removido — mecanismo nunca foi conectado ao pipeline real (Serilog é o logging efetivo); código morto (`ILoggingStrategy`/`ElasticSearch*`) e config removidos em 2026-07-27 |

### 🔴🔴 2026-08-15 — rotação da senha SQL descartada: credencial compartilhada org-wide

O dono confirmou uma restrição crítica que **invalida a linha de ação anterior** ("rotacionar"):
a senha do SQL Server (login `macgyver`, host `172.31.249.51`, banco `ConnectUS_Macgyver`)
é uma credencial **compartilhada por ~231.890 times dentro da NDD inteira**, não exclusiva deste
projeto. Trocá-la não é uma decisão que este time pode tomar unilateralmente — quebraria todo
consumidor da credencial fora deste repositório. **Rotação sai do plano de remediação.**

Isso muda o cálculo de risco: como o segredo não pode ser invalidado, o vazamento em texto plano
no histórico do git (regressão de 2026-07-18) **é permanente** enquanto o histórico não for
limpo — e os 4 repositórios da org estão públicos desde 2026-08-15, então qualquer pessoa na
internet já pode ler esses commits hoje. A resposta correta deixa de ser "invalidar a senha" e
passa a ser: (1) reduzir a exposição futura, (2) reduzir o raio de dano se a senha vazada for
usada, (3) impedir reincidência.

**Prioridades, em ordem:**

1. **[PRIORIDADE #1] Limpar o histórico do git** (`git filter-repo`/BFG, ver seção abaixo) —
   única ação que efetivamente reduz a exposição, já que a senha não pode ser trocada.
   Dono: `@lp-devops`, sob confirmação explícita do dono do projeto (reescreve histórico,
   exige re-clone de todo mundo). Antes disso, considerar também **voltar os repos a privado**
   como mitigação imediata e reversível enquanto a limpeza é preparada.
2. **Hardening da senha em repouso no host** — hoje, mesmo fora do `appsettings.json`, a senha
   fica em texto plano no `Environment` do serviço Windows (`HKLM\SYSTEM\...\Services\
   LayoutParserApi\Environment`), legível por qualquer admin local. Avaliar DPAPI
   (`ProtectedData` com `Machine` scope), Windows Credential Manager, ou
   `ProtectedConfigurationBuilder` do ASP.NET Core para criptografar a connection string em
   repouso — sem infra nova (Vault/Consul já descartados por porte do projeto). Dono:
   `@lp-devops` (host) com apoio de `@lp-backend-dev` (código, se precisar de leitura custom).
3. **Nunca logar/exibir a connection string.** Checado nesta sessão: os `LogError(ex, ...)` em
   `Services/Database/CachedMapperService.cs` e `MapperDatabaseService.cs` logam `ex.Message`,
   mas `SqlException.Message` do SqlClient não inclui a senha (só server/DB/user) — risco baixo,
   não zero. Confirmar que nenhum outro ponto loga a connection string completa (`ex.ToString()`
   em nível `Debug`/`Trace`, por exemplo). Dono: `@lp-backend-dev`.
4. **Prevenir reincidência com mecanismo técnico, não só disciplina** — a regressão de
   2026-07-18 aconteceu porque alguém testou local com a senha no `appsettings.json` e comitou
   junto. Propor: (a) hook de pre-commit local com `gitleaks`/`detect-secrets`; (b) step no CI
   (`ci-dev.yml`/`deploy.yml`) que escaneia o diff do PR por padrão de connection string com
   senha antes de permitir merge. Ambos gratuitos, sem licença. Dono: `@lp-backend-dev` (hook
   local) + `@lp-devops` (step de CI).
5. **Compartimentalizar o dano no lado do SQL** — avaliar com o DBA se o login `macgyver`, tal
   como usado por esta API, tem permissões mais amplas do que o necessário (acesso de
   escrita/DDL em bases que a API não toca). Restringir ao mínimo necessário não impede o
   vazamento, mas reduz o que alguém com a senha comprometida consegue fazer. Dono: escalar ao
   DBA (fora do alcance de qualquer agente).

**Reversão futura:** se algum dia a NDD decidir isolar este projeto com um login SQL próprio
(não compartilhado), a rotação volta a ser viável e o runbook antigo (abaixo) pode ser reativado.

### ⚠️ REGRESSÃO (2026-07-18) — senha SQL voltou ao repositório

A senha do SQL Server **reapareceu em texto plano** no `appsettings.json` comitado e entrou no
**histórico da `master` via merge da PR #7**. A remoção foi refeita em 2026-07-18 (placeholder `""`),
mas o valor está de novo em commits públicos do histórico.

- A limpeza de histórico (`git filter-repo`/BFG, seção abaixo) precisa cobrir **também** esses
  commits novos — é a única mitigação real agora que rotação está fora de cogitação (ver
  2026-08-15 acima).
- Causa raiz a vigiar: ao testar localmente com a senha no JSON, o arquivo acaba indo junto no commit.
  Use `dotnet user-secrets` (dev) ou o mecanismo de CI abaixo — **nunca** edite o segredo no `appsettings.json`.

### Plano de remediação

- [x] **Substituir** valores no `appsettings.json` por **placeholders vazios** (`""`).
- [x] **Remover** os fallbacks hardcoded (`?? "<segredo>"`) no código → `?? string.Empty`.
- [x] **Ignorar** `appsettings.*.local.json` no `.gitignore`.
- [x] **Documentar** uso de `dotnet user-secrets` (dev) e env vars `Section__Key` (prod) — ver README §9.
- [ ] ~~Rotacionar a senha do SQL Server~~ — **DESCARTADO em 2026-08-15**: credencial compartilhada
      por ~231.890 times na NDD, fora do controle deste projeto. Ver seção 2026-08-15 acima.
- [x] **Limpar o histórico do git** (`git filter-repo` / BFG) — executado em 2026-08-15
      (`@lp-devops`, sob confirmação do dono): force-push feito, repos voltaram a público.
- [ ] **Hardening em repouso** da senha no host (DPAPI/Credential Manager/`ProtectedConfigurationBuilder`) — `@lp-devops`.
      Avaliação e runbook prontos (recomendação: `ProtectedConfigurationBuilder`/user-secrets
      com DPAPI, opção C — ver [`docs/architecture/runbook-hardening-senha-sql-em-repouso.md`](../../docs/architecture/runbook-hardening-senha-sql-em-repouso.md));
      falta aplicar no host de produção (dono, via RDP) + um handoff pontual de código para
      `@lp-backend-dev` (`AddUserSecrets` fora de `IsDevelopment()`).
- [x] **Step de CI** anti-reincidência (`gitleaks`) — `.github/workflows/gitleaks.yml`, roda em
      todo PR contra `develop`/`master`/`main`, escaneando o diff introduzido pelo PR
      (`@lp-devops`). Falta a metade de `@lp-backend-dev`: hook de pre-commit local.
- [ ] **Revogar/desprovisionar** (não rotacionar) a API key do Gemini exposta — Gemini foi decomissionado, sem consumidor previsto. **Ação do dono do projeto** — ver runbook abaixo 🔴.

### Como configurar os segredos (dev)

O `UserSecretsId` já está no `.csproj`. A precedência é
`appsettings.json` → `user-secrets` (Development) → env vars → args.

```bash
dotnet user-secrets set "Database:Password" "<senha>"
dotnet user-secrets set "Gemini:ApiKey" "<key>"
# Produção: variáveis de ambiente no formato Section__Key
#   Database__Password=...  Gemini__ApiKey=...
```

### Segredos no CI de dev (`ci-dev.yml`) — mecanismo e runbook de rotação

O deploy de dev instala a API como **serviço Windows nativo** e injeta o segredo no ambiente
**do serviço** (registro `HKLM\SYSTEM\...\Services\LayoutParserApi\Environment`, `REG_MULTI_SZ`)
a partir do secret **`DB_PASSWORD_DEV`** do GitHub Actions. O valor nunca aparece em log
(o Actions mascara secrets e o workflow não ecoa o valor).

**Variables/Secrets que o operador precisa criar** (GitHub → repo `LayoutParserApi` →
Settings → Secrets and variables → Actions):

| Nome | Tipo | Valor | Obrigatório |
|------|------|-------|-------------|
| `DEPLOY_PATH_DEV` | **Variable** | `C:\inetpub\wwwroot\layoutparser` (máquina dev) | Sim — o deploy falha sem ela |
| `API_URL_DEV` | **Variable** | URL da instância dev (default `http://localhost:5100` se ausente) | Não |
| `DB_PASSWORD_DEV` | **Secret** | Senha do SQL **atual em uso** (hoje ainda a comprometida — ver nota abaixo) | Não — sem ela a API sobe degradada (sem SQL) |

> ⚠️ **Status (atualizado 2026-08-15):** a rotação da senha SQL foi **descartada** — é
> credencial compartilhada por ~231.890 times na NDD, fora do controle deste projeto (ver seção
> 2026-08-15 acima). O secret `DB_PASSWORD_DEV` continua com a senha atual (comprometida, mas
> permanente) — a mitigação real é a limpeza do histórico do git e o hardening em repouso, não a
> troca do valor.

**Runbook de rotação da senha SQL** (mantido apenas como referência histórica — **não aplicável
enquanto a senha for compartilhada org-wide**; reativar só se este projeto ganhar um login SQL
próprio no futuro):

1. No SQL Server: `ALTER LOGIN <login> WITH PASSWORD = '<nova-senha>'`.
2. No GitHub: atualizar o secret `DB_PASSWORD_DEV` (e o equivalente de produção, quando existir).
3. Redisparar o deploy (`workflow_dispatch` do CI Dev ou novo push) — o step reescreve o
   `Environment` do serviço e reinicia a API com a senha nova.
4. Validar smoke test verde e conexão SQL nos logs (sem imprimir a senha).

### Revogação da API key do Gemini — Gemini decomissionado (2026-07-21)

Decisão de arquitetura: Gemini e OpenAI foram **abandonados por completo** como provedores de LLM
neste projeto — Ollama local assume 100% do papel (loop RAG gerar → validar → corrigir, sem
fine-tuning). Motivo de fundo: dado fiscal sensível não deve sair pra nuvem sem autorização explícita
(ver "Regras gerais" abaixo). Detalhe da decisão: [memória de `@lp-architect`](../agent-memory/lp-architect/gemini-openai-decommission-decision.md).

Com o decommission, a ação sobre a chave do Gemini deixa de ser "gerar uma chave nova" (rotação) e
vira **revogar/desprovisionar de vez** — não há mais consumidor previsto, então não faz sentido reemitir.

> **Nota de risco factual (não é motivo pra baixar a prioridade da revogação):** hoje nenhum dos
> serviços que consomem a chave do Gemini (`GeminiAIService`, `SemanticAIGenerator` etc.) está
> registrado no DI em `Program.cs` — os endpoints que dependem deles quebram com exceção em runtime,
> então a chave não vaza *agora* por acidente de código. Isso não é remediação deliberada, é bug —
> a remoção desse código morto é tarefa do `@lp-backend-dev` (Dex), já mapeada em
> `docs/architecture/ai-roadmap-dispatch.md` (Grupo 1). Não muda a urgência de revogar a chave: ela
> já esteve exposta em texto plano no histórico do repo.

**Fora do alcance do `@lp-devops`:** revogar a chave exige acesso interativo ao console do provedor
(Google AI Studio / Google Cloud Console) com a conta que a gerou — **não é algo que o agente executa
via terminal.** Passos manuais para o dono do projeto:

1. Acessar [Google AI Studio → API keys](https://aistudio.google.com/app/apikey) (ou Google Cloud
   Console → APIs & Services → Credentials, se a chave foi provisionada por lá) com a conta usada
   para gerar a chave do `Gemini:ApiKey`.
2. Localizar a chave associada a este projeto e **deletar/revogar** (prefira revogar a apenas
   desativar, se a UI oferecer as duas opções — revogação impede reuso mesmo que o valor exposto
   tenha sido copiado por terceiros).
3. Confirmar, na mesma tela, ausência de uso/billing após a revogação — serve de confirmação de que
   a chave morreu, além de evitar custo residual.
4. **Não gerar chave nova.** Checado nesta sessão: não há `GEMINI_API_KEY`/secret equivalente em
   `.github/workflows/ci-dev.yml` ou `deploy.yml` — nada a limpar do lado do GitHub Actions. Se a
   decisão de decommission for revertida no futuro, gerar uma chave nova **nesse momento**, não antes.
5. Avisar `@lp-devops` (ou marcar diretamente neste arquivo) quando a revogação estiver concluída,
   para atualizar a tabela acima de 🔴 para ✅.

> ⚠️ A limpeza do histórico do git (seção abaixo) continua pendente e **também** cobre os commits
> onde a chave do Gemini apareceu em texto plano — revogar a chave não substitui essa limpeza, mas
> reduz a urgência dela especificamente para este segredo (chave morta não é mais explorável mesmo
> que ainda apareça no histórico).

### Estado-alvo recomendado (não implementado)

Migrar a conexão SQL para **autenticação integrada Windows / gMSA** (Group Managed Service Account):
elimina a senha da configuração por completo e o AD rotaciona a credencial automaticamente.
Com isso, `DB_PASSWORD_DEV`/`Database__Password` deixam de existir. Recomendação de arquitetura —
exige alinhamento com o time de infra/AD antes de qualquer mudança.

### Limpeza do histórico do git (proposta — NÃO executar sem confirmação)

Os segredos antigos **persistem nos commits anteriores** mesmo após este commit. Para removê-los:

1. **Pré-requisitos:** repo limpo (sem alterações pendentes), avisar todos que têm clone/fork,
   e ter um backup (`git clone --mirror`).
2. **Opção A — `git filter-repo`** (recomendado):
   ```bash
   pip install git-filter-repo
   # criar replacements.txt com:  <segredo-antigo>==>REMOVIDO
   git filter-repo --replace-text replacements.txt
   ```
3. **Opção B — BFG Repo-Cleaner:**
   ```bash
   bfg --replace-text replacements.txt
   git reflog expire --expire=now --all && git gc --prune=now --aggressive
   ```
4. **Force-push** a história reescrita e **invalidar** os reflogs no remoto.
   Exige coordenação: todos reclonam; PRs/branches abertos quebram.

> ⚠️ **Para a senha SQL, a limpeza É a mitigação principal** (rotação não é opção — ver seção
> 2026-08-15 acima): qualquer clone feito antes da limpeza ainda contém o segredo, mas ao menos
> deixa de ser publicamente acessível via GitHub. Para a API key do Gemini, revogação continua
> sendo a ação que efetivamente invalida o segredo — a limpeza de histórico é complementar,
> não substitui a revogação.

## CodeQL desativado — Dependabot mantido (2026-08-15)

**Causa raiz do erro de CI** (`Advanced Security must be enabled...`): CodeQL *code scanning* em
repositório **privado** exige GitHub Advanced Security (GHAS), pago. O repo está privado desde
2026-08-12. A análise roda e conclui, só o **upload do SARIF** falha — todo run do
`.github/workflows/codeql.yml` falha eternamente, sem alternativa de config (não é bug de YAML/
permissions, ver comentário já presente no arquivo). Como `SecurityCodeScan` (Roslyn, gratuito, já
ativo com `security-code-scan-baseline.json`) cobre o mesmo papel de SAST pra C#, o CodeQL é
puro desperdício de minutos de CI + ruído de "falhou" a cada push/PR/segunda-feira.

**Decisão: remover `.github/workflows/codeql.yml` por completo** (não só o step de upload — sem
GHAS o job de `analyze` não tem efeito nenhum; manter só a análise local sem upload seria rodar
`autobuild`/`security-extended` por ~30min pra descartar o resultado). Instrução exata pra
`@lp-devops`: apagar o arquivo (o `LayoutParserReact` tem workflow idêntico — mesmo repo privado,
mesma limitação de GHAS; a distinção não é "publico vs privado", é confirmar se o dono quer
remover lá também, decisão dele).

**Dependabot NÃO é o problema — mantido como está.** Confirmado lendo `.github/dependabot.yml`:
são `version updates` (PRs automáticos de bump de dependência, `nuget` + `github-actions`), que
é gratuito em qualquer repositório (público ou privado), sem GHAS. O que exige GHAS em repo
privado é uma família diferente — `Dependabot alerts` (scanning de vulnerabilidade) e `secret
scanning` — nenhum dos dois está configurado aqui (não há nada em `Settings → Security`
habilitando alerts, só o `dependabot.yml` de version updates). O dono lembrava de "parar
Dependabot por licença" — essa lembrança se aplica ao CodeQL (mesma família "Advanced Security"),
não ao Dependabot version updates. Nenhuma ação necessária no `dependabot.yml`.

**Resumo pra `@lp-devops` executar:**
1. `git rm .github/workflows/codeql.yml` (LayoutParserApi). Avaliar o mesmo no LayoutParserReact.
2. `dependabot.yml` — **não mexer**, já é 100% gratuito e correto.
3. `SecurityCodeScan` + baseline — **não mexer**, é o substituto ativo do CodeQL.

## Regras gerais (todos os agentes)

- **NUNCA** comite segredos, connection strings ou tokens.
- Ao **detectar** um segredo em texto plano (em qualquer arquivo), **pare**, sinalize ao usuário e acione `@lp-devops`. Não silencie.
- **Nunca** logue credenciais nem conteúdo sensível de documentos de cliente.
- **LLM em nuvem (Gemini/OpenAI):** não envie documentos/dados reais de cliente sem autorização explícita. Prefira **Ollama local** para dados sensíveis.
- CORS está liberado para origens específicas em `Program.cs` — não abra para `*` em produção.
- **Identidade vem do BFF, não há `[Authorize]` ainda.** A API não autentica ninguém diretamente:
  `Services/Security/TrustedIdentityMiddleware.cs` lê os headers `x-iis-user`/`x-iis-roles`
  (configuráveis via `Security:TrustedUserHeader`/`Security:TrustedRolesHeader`) injetados pelo BFF
  Fastify (`LayoutParserReact/server/`, autenticação Entra OIDC) e popula `ICurrentUser` +
  `HttpContext.User`. Só confia nesses headers se a origem da requisição for **loopback**
  (`TrustIdentityFromLoopbackOnly`, default `true`, deliberadamente fora do `appsettings.json`) —
  isso fecha forja de identidade mesmo com a API respondendo em `0.0.0.0`. Nenhum endpoint tem
  `[Authorize]`/enforcement por papel ainda — é decisão de produto em aberto. Detalhe e status:
  [`docs/architecture/rollout-p2-autenticacao.md`](../../docs/architecture/rollout-p2-autenticacao.md).
- **`ApiKeyGateFilter`/`ApiKeyGatePolicy` foram removidos** (branch `feat/identidade-do-bff`,
  commit `c7489ca`) — a chave compartilhada deixou de ser o mecanismo de defesa da fronteira
  BFF↔API; a defesa hoje é rede (API só deve escutar `127.0.0.1`, em andamento por `@lp-devops`) +
  a guarda de loopback do middleware acima. Não reintroduza `Security:ApiKey`/`Security:AnonymousPaths`.
