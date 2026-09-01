# Design — Slice 7: Governança/Publicação + Piloto FIAT (issue #94, seções 12/14)

> Autora: `@lp-architect` (Aria) · 2026-09-01 · Só design, sem código. Último slice da
> fundação. Depende de Slice 5 (`MappingRelease` em `draft_compiled`/`test_passed`/`test_failed`)
> e Slice 6 (gate Sysmiddle já confirmado não-vetor).

## 1. Máquina de estados de `MappingRelease` (estende Slice 5)

```
draft_compiled → test_passed ─┬→ InReview → Approved → Published → Deprecated → Archived
                 test_failed ─┘                              ↑
                                                          rollback (nova transição)
```

- `test_failed` bloqueia entrada em `InReview` (gate obrigatório, campo `RequiredGatesPassed`
  já contratado no Slice 5).
- `Approved`/`Published` são estados **novos** deste slice, adicionados ao enum existente —
  não reabrir o enum do Slice 5, só estender.
- Imutabilidade: `Published` congela `Artifacts[]`; qualquer edição manual de TCL/XSL/XSLT
  cria um novo `MappingRelease` (nova revisão), nunca muta o publicado — reaproveita o
  `DraftId`/hash de snapshot do Slice 5 para detectar "é revisão nova ou é o mesmo compile".
- Nova revisão exige regressão: `Approve`/`Publish` de uma revisão N+1 exige um `test-runs`
  novo com `RequiredGatesPassed=true` — não herda o resultado da N.

Novos campos em `MappingRelease`:
```
Environment: development | validation | production   // onde está ativo, não onde foi testado
ApprovedByUserId / ApprovedAt / ApprovalJustification
PublishedByUserId / PublishedAt
PreviousPublishedReleaseId   // snapshot no momento do publish, ver §3
```
`MappingTransition` (nova tabela, não reaproveitar log genérico): `ReleaseId, FromStatus,
ToStatus, ActorUserId, OccurredAt, Justification, ChecksSnapshot` — obrigatório por transição,
cobre "ator/instante/checks/justificativa" da spec.

## 2. RBAC mínimo (escopo deste slice, não RBAC genérico)

Hoje: zero `[Authorize]` em qualquer controller; `ICurrentUser` já resolve identidade via BFF.
Decisão: introduzir `[Authorize]` **só** nos 3 endpoints novos abaixo, papel checado contra
`WorkspaceMembership.Role` do workspace do release (não claim global). `Reviewer`/`FiscalAdmin`
aprovam; só `FiscalAdmin`/`Owner` publicam e revertem (rollback tem blast radius de produção).
Isso é o mínimo pra spec §12 ("aprovação e promoção exigem RBAC") sem abrir a issue #94 inteira
(CRUD de mapeador por `admin`) — a policy nasce escopada a `MappingRelease`, reaproveitável
depois quando #94 for implementada, não o contrário.

## 3. Rollback

Promove a versão anterior **publicada** (não a imediatamente anterior por número) —
`PreviousPublishedReleaseId`, gravado no momento do `publish` como snapshot de "o que estava
`Published` antes desta transição", evita depender de reconstruir isso a partir do histórico de
`MappingTransition` em runtime (mais simples, mais barato, auditável do mesmo jeito porque a
transição de rollback também grava `MappingTransition`). Rollback: `Published` atual →
`Deprecated`; `PreviousPublishedReleaseId` → `Published`. Endpoint idempotente (rollback duas
vezes seguidas é no-op, não encadeia).

## 4. Endpoints novos

```
POST /api/workspaces/{workspaceId}/mapping-releases/{releaseId}/approve   [Reviewer|FiscalAdmin]
  Body: { justification }. Exige status test_passed. → InReview→Approved.

POST /api/workspaces/{workspaceId}/mapping-releases/{releaseId}/publish   [FiscalAdmin|Owner]
  Exige Approved + Environment target. → Published; grava PreviousPublishedReleaseId.

POST /api/workspaces/{workspaceId}/mapping-releases/{releaseId}/rollback  [FiscalAdmin|Owner]
  Sem body. Idempotente conforme §3.
```
Padrão ETag/idempotência do Slice 3, ` MappingEngineGuardFilter` não se aplica (sem campo
`engine` mutável nestes contratos).

## 5. Teste ponta a ponta FIAT

Não automatizar o pipeline inteiro num único teste caro. Dividir:
- **Integração automatizada (CI):** um teste por transição do gate FIAT (§14) usando fixture
  sintética/sanitizada (não os artefatos reais do React `.codex/temp/teste` — nunca sobem ao
  Git). Cobre inventário→draft→compile→test-run→approve→publish com asserts de estado, não de
  conteúdo fiscal real.
- **Runbook manual documentado** (não código): execução única com os artefatos FIAT reais,
  local, evidenciando XML válido contra XSD real e diff contra gabarito quando este chegar
  (spec §14 lista Excel/XSD/gabarito como "ainda faltam" — bloqueio externo, não deste slice).
  Resultado vira anexo de auditoria, não teste repetível em CI.

## Primeiro passo executável

`@lp-backend-dev`: estender enum de `MappingRelease.Status` + criar `MappingTransition` +
os 3 endpoints com `[Authorize]` por `WorkspaceMembership.Role`. `@lp-qa` desenha as fixtures
sintéticas do gate FIAT em paralelo.
