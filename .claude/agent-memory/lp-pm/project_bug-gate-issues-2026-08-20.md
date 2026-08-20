---
name: project-bug-gate-issues-2026-08-20
description: Issues #171-#174 (bug-to-issue/gate-to-issue) do achado de @lp-architect em 2026-08-20, 4 TODOs confirmados no código
metadata:
  type: project
---

Missão `bug-to-issue`/`gate-to-issue`, 2026-08-20 — 4 TODOs achados por `@lp-architect` na varredura, confirmados por Grep exato antes de formalizar (linha de origem citada em cada issue):

- **#171** tech-debt "tipo de documento hardcoded como NFe" — `AutomatedTransformationTestService.cs:189` + `TransformationValidatorService.cs:77`. Dono `@lp-parser-llm`.
- **#172** story "implementar leitura de PDF de orientações XSD" — `XsdValidationService.cs:379`. Dono confirmou explicitamente que PDF é escopo real (não código morto) — corpo mais detalhado, com avaliação de licença de biblioteca (iTextSharp AGPL vs PdfSharp) como ponto de atenção. Dono natural `@lp-backend-dev` (a reavaliar se precisar de `@lp-parser-llm` para matching semântico erro↔trecho do PDF).
- **#173** tech-debt "validação mais detalhada" — `TransformationValidatorService.cs:201`. TODO curto sem contexto; item menor. Dono `@lp-parser-llm`.
- **#174** tech-debt "GetLearningSummary não busca modelos reais" — `MetricsController.cs:127`. Dono `@lp-backend-dev`.

Todas criadas direto (fonte = diagnóstico técnico documentado de `@lp-architect`, sem duplicata encontrada na busca prévia) e adicionadas ao Project #2 (Status=Todo; Tipo/Dono conforme acima). Ver [[reference-gh-cli-setup]] para os field-ids usados.
