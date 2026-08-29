---
name: issue-86-timeline-pre-fix-e-gap-contrato
description: LayoutParserReact #86 (candidates:[] CNHI) descreve estado de código de antes do fallback de IA (c65157d, 2026-08-16); causa real remanescente é contrato de diagnóstico, não bug novo
metadata:
  type: project
---

Issue LayoutParserReact #86 foi aberta em 2026-08-12T23:04, mesmo dia da investigação de 4
capítulos em `.claude/agent-memory/lp-backend-dev/execute-candidates-ausencia-total-para-cnhi-envnfe.md`
para o mesmo layout (`LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe`). As duas mensagens citadas na issue
batem com o código *pré-fix*: SQL engolido virando `Applicable=false` e convenção errada de path
do `.tcl` — ambos corrigidos no mesmo dia. Mais importante: o pathway IA em `execute-candidates`
(issue #40) só foi cabeado em `c65157d` (2026-08-16), **4 dias depois** da issue #86 — então a
reprodução que originou a issue não podia ter visto o warning de fallback de IA porque ele ainda
não existia.

**Why isso importa:** ao reabrir uma issue antiga que cita sintoma já investigado, checar a DATA
de abertura contra o `git log` dos fixes relevantes antes de tratar como regressão — pode ser
simplesmente uma issue que ficou aberta enquanto o código evoluiu por baixo dela.

**Achado incidental (gap real, não da issue):** sanitização de mensagens (`LowCodeErrorSanitizer.ForWire`)
só é aplicada no pathway sysmiddle (`TransformationExecutionController.cs:385`) — o pathway tcl-xsl
(linhas 548, 571, 587) usa `ex.Message`/`pipelineResult.Errors` crus, que podem conter caminho
absoluto de `IOException`/`XmlException` internas de `TransformationPipelineService`. Não é a causa
da issue #86, mas é regressão de segurança latente a corrigir junto de qualquer mudança no
contrato desse endpoint.

**How to apply:** diagnóstico completo + desenho do contrato `pathwayDiagnostics[]` em
`docs/architecture/diagnostico-issue-86-diagnostico-estruturado-execute-candidates.md`. Antes de
despachar como "novo bug", reproduzir com log/CorrelationId real (o warning de fallback de IA
deveria aparecer hoje; se não aparecer, checar `IAiFallbackSuppressionGate` em runtime, não só
ler código estático).

Relacionado: [[../lp-backend-dev/execute-candidates-ausencia-total-para-cnhi-envnfe]],
[[../lp-pm/project_execute-candidates-cnhi-gap]].
