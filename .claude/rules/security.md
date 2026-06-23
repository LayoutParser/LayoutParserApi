---
description: Regras de segurança e a pendência crítica de segredos versionados.
---

# Segurança — LayoutParser API

## Segredos versionados — status da remediação

Os segredos estavam em texto plano no [`appsettings.json`](../../appsettings.json) **e** em fallbacks
hardcoded no código (`GeminiAIService`, `LayoutDatabaseService`, `ElasticSearchLogger`).

| Segredo | Onde | Status |
|---------|------|--------|
| API key do **Gemini** | `Gemini:ApiKey` | Removido do código/JSON ✅ · **rotacionar (comprometida)** 🔴 |
| Senha do **SQL Server** | `Database:Password` | Removido do código/JSON ✅ · **rotacionar (comprometida)** 🔴 |
| Credenciais do **Elastic** | `ElasticSearch:Username/Password` | Removido do JSON e da senha hardcoded ✅ · rever 🟡 |

### Plano de remediação

- [x] **Substituir** valores no `appsettings.json` por **placeholders vazios** (`""`).
- [x] **Remover** os fallbacks hardcoded (`?? "<segredo>"`) no código → `?? string.Empty`.
- [x] **Ignorar** `appsettings.*.local.json` no `.gitignore`.
- [x] **Documentar** uso de `dotnet user-secrets` (dev) e env vars `Section__Key` (prod) — ver README §9.
- [ ] **Rotacionar** as chaves expostas (gerar novas no provedor/banco) — **ação do operador**. 🔴
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
