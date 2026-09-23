---
name: reauditoria-backlog-2026-09-02
description: Segunda auditoria do dia 2026-09-02 — 6 issues movidas p/ In Progress com evidência de branch local não mergeada, 4 PRs Slice confirmados merged, board final 43 Done/17 Todo/6 In Progress.
metadata:
  type: project
---

Segunda auditoria de backlog no mesmo dia (2026-09-02), depois de trabalho de `@lp-parser-llm`
em várias branches locais não pushadas. Memória da auditoria original do dia não foi encontrada
no path esperado (pode ter sido salva sob outro nome) — refeita checagem do zero via `gh issue
list --state all` + `git branch --list`.

**Achado central:** havia várias branches `feat/*`/`fix/*` locais (nunca pushadas pro remoto)
implementando issues abertas. Regra aplicada: implementação existe mas não mergeada em
`develop` → **comentar linkando branch/commit, mover pra "In Progress" no Project #2, NÃO
fechar a issue.**

Issues movidas Todo → In Progress (comentadas com branch+commit):
- #95 (security export/{id} sem Authorize) — branch `fix/export-authorize-e-metrics-learning-summary`, commit `55a5b15`.
- #174 (MetricsController.GetLearningSummary) — mesmo commit `55a5b15` (resolve os dois juntos).
- #172 (leitura PDF orientações XSD) — branch `feat/pdf-diagnostico-xsd-172`, commit `4148906`.
- #99 (instrumentar transformationsTicket) — branch `feat/instrumentacao-parse-ticket-99`, commit `ca801ac`.
- #98 (prompt customizado) — branch `feat/ia-prompt-customizado-98`, commit `35c85e3` (nota: branch teve um revert intermediário do commit de #95/#174, mas o commit de #98 ficou íntegro).
- #102 (schema SQL AiUserSession) — branch `feat/ai-user-session-schema-102`, commit `59178f9`.

Issues checadas e confirmadas **sem trabalho novo** (permanecem Todo, sem ação): #90, #96, #97,
#103, #104, #137, #151, #173. Buscas por `git log --all --grep "issue #N"` não retornaram nada
pra essas — branch `fix/reconcilia-best-effort-issue-140` (nome sugeria #151) na verdade não tem
nenhum commit à frente de `develop`, já estava mergeada antes.

PRs #264 (Slice3 MappingDraft), #265 (Slice2 FiscalMappingPackage, issue #229), #267 (Slice7
governança piloto FIAT) confirmados `MERGED` em 2026-09-02 20:15 UTC via `gh pr view --json
mergedAt`. As issues-guarda-chuva #225-#232 (todos os Slices) já estavam `CLOSED` **antes** do
merge (2026-08-31/09-01) — closure baseado em evidência de implementação, não em merge; padrão
já visto antes (ver [[project_board-sync-2026-08-18]]), sem regressão a corrigir aqui porque o
merge de fato aconteceu depois. PR #263 (Slice5) ficou `CLOSED` sem merge — obsoleto, confirmado.

Board final Project #2: **43 Done / 17 Todo / 6 In Progress** (66 itens totais).

**Não verificado nesta rodada:** dependências cross-repo (LayoutParserReact/#221/#218/#219) —
sem acesso a esses repos, mantidas como estavam.
