---
name: retraining-automatizado-f4-2026-09-08
description: Issue #351 (F4.1-F4.3 retraining automatizado) criada a partir do ADR de backfill/retraining; backfill em lote explicitamente não formalizado.
metadata:
  type: project
---

Issue #351 formaliza F4 completo (F4.1 telemetria + F4.2 gatilho por volume/90 dias + F4.3
lock+validação held-out) como uma única entrega faseada, seguindo a recomendação exata do ADR
`docs/architecture/adr-backfill-catalogo-e-retraining-automatizado-2026-09-08.md`
(branch `docs/adr-backfill-retraining-automatizado`, não mergeada em `develop` no momento da
criação). Continuação de #151 (comentário linkando adicionado).

**Por que não abri issue de backfill:** o ADR concluiu que backfill em lote sobre o catálogo
inteiro não é viável — não por escala/CPU (catálogo é pequeno, ≤200 layouts), mas por ausência de
corpus real de instância por layout (de 54 pares candidatos held-out, só 4 produzem `<NFe>`, e o
único TXT real conhecido não bate com o schema TCL do dataset). O ADR foi explícito: "não
recomendo issue para backfill em lote agora". Segui a recomendação.

**Why:** dono pediu para formalizar só o que o ADR desenhou como viável; não inferir escopo além
do documentado.
**How to apply:** se o corpus real crescer no futuro e alguém pedir para reabrir a ideia de
backfill, checar primeiro se esse ADR ainda reflete a realidade (corpus pode ter mudado) antes de
formalizar nova issue.
