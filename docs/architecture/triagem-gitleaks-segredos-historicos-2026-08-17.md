# Triagem gitleaks — segredos históricos (2026-08-17)

## Decisão crítica: causa raiz é bug de configuração, não dívida nova

O `gitleaks.yml` faz `git fetch origin <base> --depth=1` e depois escaneia
`origin/<base>..HEAD`. Como o checkout já rodou com `fetch-depth: 0` (histórico
completo), mas o fetch do branch-base é raso (`--depth=1`), o `origin/<base>`
fica desconectado do grafo completo — o git não acha merge-base real e o range
degenera para **todo o histórico alcançável por HEAD**, não o diff da PR.
Confirmado: `d4544ba` (commit de 2025-11-07 com o achado #1) já é ancestral de
`develop`/`HEAD` hoje — ou seja, esses commits **já estão mesclados há muito
tempo**, e só aparecem no scan por causa do `--depth=1` quebrado, não porque a
PR de hoje introduziu algo.

**Recomendação para `@lp-devops`:** trocar `--depth=1` por fetch completo do
branch-base (ou usar `fetch-depth: 0` também nesse fetch, ou `git fetch origin
<base>` sem `--depth`). Isso sozinho resolve o bloqueio do PR de hoje sem
precisar de allowlist nenhuma — o scan volta a cobrir só o diff real.

## Achados reais vs. ruído

1. **`sqlserver-connection-string-with-password`** (itens 1, 4, 6) — senhas de
   SQL em commits de 2025-10-30 a 2025-11-07, anteriores à remediação de
   2026-07-18/2026-08-15. A limpeza de ontem (`git filter-repo
   --replace-text`) tratou apenas **uma** senha específica
   (`eb8XNsww3D@U&HyZe4`, a mais recente). Sem reproduzir valores, os hashes de
   commit (`d4544ba`, `9b7f5b2`, `70a7ece`) são anteriores e não foram cobertos
   pelo replacement de ontem — provavelmente é a mesma família de credencial
   (login `macgyver`) mas **não dá pra confirmar sem comparar valor a valor**,
   o que evitei fazer em texto claro. Tratamento: já coberto pela decisão de
   2026-08-15 (rotação descartada, credencial compartilhada; mitigação é
   limpeza de histórico completa, ainda pendente de segunda passada).
2. **`gcp-api-key` (Gemini)** — item 2, bate com a chave já documentada em
   `rules/security.md` como comprometida e pendente de **revogação manual**
   pelo dono via Google AI Studio (não rotação). Nenhuma ação nova; reforça
   urgência — está confirmada em histórico público.
3. **Fallback hardcoded da API key** (item 5, `GeminiAIService.cs`) — arquivo
   **já foi removido do código atual** (commit `7bc9e0d`, "remove o subsistema
   Gemini do repositório"). Achado é puramente histórico, sem risco de
   reincidência via novo commit.
4. **Falsos positivos genuínos** (itens 3 e 7) — confirmado lendo
   `.gitleaks.toml`: a regra `generic-password-assignment` é
   `"(Password|Pwd|ApiKey|Secret|ConnectionString)"\s*:\s*"[^"\s]{4,}"` — não
   distingue o marcador de redação `***SENHA_REMOVIDA***` (nosso próprio
   processo) nem `"localhost:6379"` (Redis sem senha) de um segredo real.
   Recomendação: adicionar allowlist `regexes` no `.gitleaks.toml` para
   `\*\*\*SENHA_REMOVIDA\*\*\*` e para valores de `ConnectionString` que
   batem só com `localhost:\d+` (sem `Password=`/`Pwd=`).

## Resumo para `@lp-devops`

1. **Prioridade #1 (desbloqueia PRs de hoje):** corrigir o fetch raso em
   `gitleaks.yml` (`--depth=1` → fetch completo do branch-base).
2. Adicionar allowlist em `.gitleaks.toml` para o marcador de redação e para
   `localhost:6379` sem senha (reduz ruído mesmo depois do fix do fetch, já
   que histórico antigo ainda existe até a segunda limpeza).
3. Sem ação de código nova — API key Gemini já tem runbook de revogação
   pendente no dono; connection strings antigas já cobertas pela decisão de
   2026-08-15 (limpeza de histórico ainda incompleta, precisa de segunda
   passada do `filter-repo` cobrindo os commits de 2025-10/11, não só o mais
   recente).
