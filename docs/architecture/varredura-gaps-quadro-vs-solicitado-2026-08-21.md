# Varredura — gaps entre o que foi decidido/descoberto e o quadro real (2026-08-21)

> Autoria: `@lp-architect` (Aria). Missão `review-arch`. Não implementa, não abre issue —
> mapeia gaps para `@lp-pm` formalizar.

## Método

1. `gh issue list --state open --limit 200` (25 issues abertas hoje, #88 até #174).
2. `git log --since="15 days ago" -- docs/architecture/` (~50 commits de diagnóstico/decisão).
3. Releitura dos índices de memória de todos os agentes (`.claude/agent-memory/*/MEMORY.md`).
4. Busca cruzada por palavra-chave (`gh issue list --search ...`) para os temas de risco
   conhecidos: readiness/deploy, hardening de senha, pre-commit hook, CodeQL, AiCandidateStore.

## Segurança

| Gap | Onde | Issue | Prioridade |
|---|---|---|---|
| Hardening da senha SQL em repouso (DPAPI/`ProtectedConfigurationBuilder`) — runbook pronto (`docs/architecture/runbook-hardening-senha-sql-em-repouso.md`), falta aplicar no host + handoff de código (`AddUserSecrets` fora de `IsDevelopment()`) | `security.md` checklist, item não marcado | **Nenhuma** | **Alto** — é a peça que falta pra fechar a remediação da senha SQL comprometida (rotação está descartada; histórico já foi limpo). Sem hardening em repouso, a senha continua legível em texto plano no `Environment` do serviço Windows por qualquer admin local. |
| Hook de pre-commit local (`gitleaks`/`detect-secrets`) — metade do item "prevenir reincidência"; a metade de CI (`.github/workflows/gitleaks.yml`) já existe | `security.md` checklist | **Nenhuma** | Médio — rede de segurança já existe no CI; o hook local é defesa em profundidade, não bloqueante. |
| `GET export/{id}` devolve `DecryptedContent` sem `[Authorize]` | achado 2026-08-15 | **#95 (aberta)** | Já rastreado — sem gap. |
| CodeQL/GHAS removido do CI (decisão 2026-08-15) | `security.md` | — | Já executado (`308ea97`), sem gap. |
| Revogação da chave Gemini | `security.md` | — | Concluída pelo dono em 2026-08-17, sem gap. |

## Deploy/Infra

| Gap | Onde | Issue | Prioridade |
|---|---|---|---|
| Config drift `appsettings.json` produção vs. repo — auditoria + deploy "congela" config do host | commit `bd15dca` e diagnósticos subsequentes | **#108 (aberta)**, com `#110`/`#112` como próximos passos B1/B2 | Já rastreado — sem gap. |
| Diagnóstico "deploy abortado — readiness sem resposta" (2026-08-15, `40426fd`), correlacionado ao `ValidateOnStart` da PR #114 | `docs/architecture/diagnostico-deploy-abortado-readiness-2026-08-15.md` | **Nenhuma issue própria** — está descrito como hipótese líder num doc, não formalizado | **Alto** — é causa-raiz plausível de indisponibilidade de deploy em produção, mas só existe como narrativa de diagnóstico; sem issue, corre risco de reincidir sem rastreamento. Distinto de #67 (CLOSED, causa diferente: warmup single-run). |
| `candidates: []` para LAY_CNHI (2026-08-20) | diagnóstico mais recente (`64a158a`) | Bloqueado em reprodução com `CorrelationId` real — não há ação de código pendente | Baixo — não é gap de rastreamento, é bloqueio legítimo aguardando dado do front. |

## Contrato API/Front

| Gap | Onde | Issue | Prioridade |
|---|---|---|---|
| PBI #128/Epic #126 (fieldMappings TXT↔XML) | plano de execução `a0fef11` | **#137-#141 abertas** (guarda-chuva #137 + fases #138/#139/#140/#141), #151 como Fase 4 | Já rastreado em profundidade — sem gap. |
| `transformationsTicket` — instrumentar/documentar o "trava em 100%" do front | `docs/architecture` (achado 2026-08-15) | **#99 (aberta)** | Já rastreado — sem gap. |

## Domínio parsing/IA

| Gap | Onde | Issue | Prioridade |
|---|---|---|---|
| `AiCandidateStore` sem TTL/limite | achado `@lp-qa`, issue original | **#51 fechada, mas auditoria de 2026-08-14 (`audit-2026-08-14-di-regression.md`) registra que o leak segue presente no código** | **Alto** — issue fechada sem fix real aplicado; risco de crescimento indefinido de disco/memória em produção segue ativo, mas o board mostra "resolvido". Reabrir ou criar nova issue é decisão de `@lp-pm`. |
| Particionamento de `AiCandidateStore` por usuário (pré-requisito de RBAC real, #97) | `rbac-scope-xml-generic-2026-08-14.md` | Coberto parcialmente por #92/#97 mas a dependência explícita ("abrir RBAC sem particionar cria vazamento entre usuários") não está no corpo de nenhuma issue | Médio — risco de segurança silencioso se #97 for implementada sem essa nota. |
| Geração de mapeamento fiscal via two-step (layout + gabarito SEFAZ) | `session-artifacts-sharing-design.md` (2026-08-14) | **#103 (aberta)** | Já rastreado — sem gap. |
| Hipótese Roslyn do dono contradita pela amostra real da DSL (2026-08-16) | `dsl-mapper-roslyn-hypothesis-2026-08-16.md` | Pergunta em aberto para o dono, sem issue — decisão de arquitetura ainda não tomada | Médio — bloqueia decisão sobre o parser da DSL do mapper; não é bug, é decisão pendente do dono, mas sem rastreamento formal ela pode se perder. |

## Top gaps (resumo para ação)

1. **`AiCandidateStore` — issue #51 fechada, leak real ainda presente no código** (confirmado por auditoria 2026-08-14). Risco: board mente sobre o estado real. **Alto.**
2. **Hardening da senha SQL em repouso** — runbook pronto, nada aplicado, nenhuma issue. É a peça final da remediação de um segredo permanentemente comprometido. **Alto.**
3. **Diagnóstico "readiness sem resposta" (correlação com PR #114/`ValidateOnStart`)** — causa-raiz plausível de indisponibilidade de deploy só existe como doc, sem issue. **Alto.**
4. **Hook de pre-commit local (gitleaks)** — metade do item de "prevenir reincidência" de segredos, sem issue. **Médio.**
5. **Dependência AiCandidateStore↔RBAC** — nota de risco enterrada em memória, não no corpo de #92/#97. **Médio.**
6. **Decisão Roslyn vs. parser custom da DSL do mapper** — pergunta pendente ao dono, sem rastreamento. **Médio.**

Todos os demais itens levantados nos últimos 15 dias de `docs/architecture/` (deploy, config
drift, fieldMappings/sectionMappings, governança de mapeadores, sessão de usuário) já têm
issue correspondente aberta e atualizada — o board está, no geral, bem sincronizado com o
trabalho de diagnóstico recente.
