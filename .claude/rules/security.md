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
| Senha do **SQL Server** | `Database:Password` | **REGRESSÃO em 2026-07-18** (ver abaixo) · removido de novo ✅ · **rotacionar (comprometida 2x)** 🔴 |
| Credenciais do **Elastic** | `ElasticSearch:Username/Password` | Removido do JSON e da senha hardcoded ✅ · rever 🟡 |

### ⚠️ REGRESSÃO (2026-07-18) — senha SQL voltou ao repositório

A senha do SQL Server **reapareceu em texto plano** no `appsettings.json` comitado e entrou no
**histórico da `master` via merge da PR #7**. A remoção foi refeita em 2026-07-18 (placeholder `""`),
mas o valor está de novo em commits públicos do histórico.

- **Rotação continua PENDENTE e ficou ainda mais urgente** 🔴 — a exposição agora inclui a master.
- A futura limpeza de histórico (`git filter-repo`/BFG, seção abaixo) precisará cobrir **também** esses commits novos.
- Causa raiz a vigiar: ao testar localmente com a senha no JSON, o arquivo acaba indo junto no commit.
  Use `dotnet user-secrets` (dev) ou o mecanismo de CI abaixo — **nunca** edite o segredo no `appsettings.json`.

### Plano de remediação

- [x] **Substituir** valores no `appsettings.json` por **placeholders vazios** (`""`).
- [x] **Remover** os fallbacks hardcoded (`?? "<segredo>"`) no código → `?? string.Empty`.
- [x] **Ignorar** `appsettings.*.local.json` no `.gitignore`.
- [x] **Documentar** uso de `dotnet user-secrets` (dev) e env vars `Section__Key` (prod) — ver README §9.
- [ ] **Rotacionar** a senha do SQL Server exposta (gerar nova no banco) — **ação do operador, bloqueada/escalada ao DBA** 🔴.
- [ ] **Revogar/desprovisionar** (não rotacionar) a API key do Gemini exposta — Gemini foi decomissionado, sem consumidor previsto. **Ação do dono do projeto** — ver runbook abaixo 🔴.
- [ ] **Limpar o histórico do git** (`git filter-repo` / BFG) — **executar via @lp-devops, sob confirmação**.

### Como configurar os segredos (dev)

O `UserSecretsId` já está no `.csproj`. A precedência é
`appsettings.json` → `user-secrets` (Development) → env vars → args.

```bash
dotnet user-secrets set "Database:Password" "<senha>"
dotnet user-secrets set "Gemini:ApiKey" "<key>"
dotnet user-secrets set "ElasticSearch:Password" "<senha>"   # se usar Elastic
# Produção: variáveis de ambiente no formato Section__Key
#   Database__Password=...  Gemini__ApiKey=...  ElasticSearch__Password=...
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

> ⚠️ **Status da rotação:** a rotação da senha SQL segue **PENDENTE** — está **bloqueada e
> escalada ao DBA**. Por necessidade operacional, o secret `DB_PASSWORD_DEV` contém hoje a
> senha atual (comprometida). Assim que o DBA rotacionar, execute o runbook abaixo.

**Runbook de rotação da senha SQL** (a executar quando a rotação for desbloqueada):

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

> ⚠️ **A limpeza NÃO substitui a rotação.** Qualquer clone feito antes da limpeza ainda contém os
> segredos. Só a **rotação** (gerar chaves novas) invalida o que já vazou — faça-a primeiro.

## Regras gerais (todos os agentes)

- **NUNCA** comite segredos, connection strings ou tokens.
- Ao **detectar** um segredo em texto plano (em qualquer arquivo), **pare**, sinalize ao usuário e acione `@lp-devops`. Não silencie.
- **Nunca** logue credenciais nem conteúdo sensível de documentos de cliente.
- **LLM em nuvem (Gemini/OpenAI):** não envie documentos/dados reais de cliente sem autorização explícita. Prefira **Ollama local** para dados sensíveis.
- CORS está liberado para origens específicas em `Program.cs` — não abra para `*` em produção.
- A app não usa autenticação no pipeline atual (`UseAuthorization` comentado) — sinalize se um endpoint novo expuser dado sensível sem proteção.
