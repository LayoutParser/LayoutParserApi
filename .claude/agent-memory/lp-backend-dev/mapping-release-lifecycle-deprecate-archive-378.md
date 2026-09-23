---
name: mapping-release-lifecycle-deprecate-archive-378
description: Endpoints deprecate/archive de MappingRelease (issue #378) — regras de transição e fakes espalhados
metadata:
  type: project
---

Issue #378 (branch `feat/mapping-release-archive-378`, base `develop`): adicionados
`POST .../mapping-releases/{releaseId}/deprecate` e `/archive` no `MappingGovernanceController`.

Regras de transição implementadas (no `SqlMappingReleaseStore`, espelhadas no fake do teste):
- `deprecate`: só `published → deprecated`. Idempotente no-op se já `deprecated`. Outra origem → `InvalidOperationException` → 422.
- `archive`: origem em `{deprecated, test_failed, in_review}` → `archived`. Idempotente no-op se já `archived`. `published` → 422 (tem que deprecar antes).
- RBAC `[RequireWorkspaceRole(FiscalAdmin, Owner)]` (igual publish/rollback).
- Body opcional `LifecycleTransitionRequest { Justification }`.

**Why:** cross-check #198.1 — os status existiam no enum mas sem endpoint que os disparasse.

**How to apply:** `IMappingReleaseStore` é implementado por **4 fakes** em tests, não só o de
`MappingGovernanceControllerTests`: `MappingCompileServiceTests`, `MappingTestRunServiceTests`,
`Integration/FiatPilotGateTests`. Qualquer método novo na interface exige stub nos 4 senão o
build de teste quebra (CS0535). Ver [[gates-auditoria-enforcement-2026-08-12]].
