---
name: pr-201-issue-139-mapper-vo-canonico
description: PR #201 (issue #139) consolidou RealMapperParser como parser MapperVO canônico, todos os checks verdes de primeira, sem falso positivo SCS0018
metadata:
  type: project
---

PR #201 (`feat/mapper-vo-parser-comparacao-139` → `develop`) consolidou `RealMapperParser`
como parser canônico de `MapperVO`, migrou `XslGeneratorService`, depreciou (não removeu) o
parser legado, e corrigiu um segundo uso residual achado pelo QA. 4 commits: `23dd1f8`
(sombra log-only), `95a0f9f` (migração + `[Obsolete]`), `a8fb0a8` (doc), `a9633ab` (fix QA).

Todos os 4 checks (`build`, `build-and-test`, `dependency-review`, `gitleaks-scan`) passaram
de primeira — diferente de [[pr-198-ci-scs0018-bloqueado]] e [[pr-200-ci-scs0018-bloqueado]],
que caíram no falso positivo SCS0018 por deslocamento de linha no baseline.

**Why:** registra que nem toda PR cai nesse padrão — útil para não assumir que todo PR vai
precisar de correção manual de baseline SCS0018.

**How to apply:** se aparecer de novo o padrão SCS0018 por deslocamento, o diagnóstico já
documentado em `pr-198`/`pr-200` continua válido; aqui não foi necessário.

Prepara terreno para a issue #140 (bloqueada até esta PR ser validada e mergeada pelo dono —
merge NÃO foi feito, fica a critério do dono).
