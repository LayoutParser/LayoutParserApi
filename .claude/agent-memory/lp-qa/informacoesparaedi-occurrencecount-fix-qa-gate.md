---
name: informacoesparaedi-occurrencecount-fix-qa-gate
description: QA gate do fix Bug A (Length real) + OccurrenceCount/IsAggregatedOccurrence em ParsedField (commit a330af2) — PASS validado contra amostra real, não short-circuit
metadata:
  type: project
---

Fix de `InformacoesParaEDI`/LINHA081 (Lia, commit `a330af2`, worktree
`agent-a08c990d4efceeb06`) validado com a amostra real (`.claude/tmp/26072026/`, copiada de
`.claude/temp/teste/`) — os 4 testes de `PositionalFormatRegressionTests` rodaram de verdade
(sem short-circuit) e passaram, mais suíte completa (382/382).

**Achado durante a validação:** a contagem de baseline do MQSeries de controle estava errada em
704 — o valor real e correto é **705**. Confirmado isolando a causa: rodei o mesmo teste contra o
worktree PAI (950cdf9, commit anterior ao fix da Lia) com a mesma amostra real e ele também
produzia 705, não 704. Ou seja, o "704" nunca tinha sido validado contra dado real (herdado de um
período em que o teste sempre fazia short-circuit) — não é regressão introduzida pelo fix do Bug
A/OccurrenceCount. Corrigi o assert para 705 e recapturei `MqBaselineSha256` =
`453e9a184e253d1b310f7814282ebfddb9ca5a99f25acc65ecae741060c8ecfd` (script: adicionar
temporariamente `File.WriteAllText` do hash completo, rodar, copiar valor, reverter o
write temporário — `Assert.Equal` do xUnit trunca strings longas na mensagem de falha).

**Why:** confiar apenas no assert de contagem sem isolar a causa (fix novo vs. comportamento
pré-existente) teria devolvido um falso "achado" pra Lia corrigir algo que já estava certo.

**How to apply:** ao validar hash/contagem baseline "STALE" documentado como pendente de
recaptura, sempre isolar se o valor divergente é efeito do fix em revisão ou já preexistia —
usar `git worktree add <commit-pai>` num scratchpad e rodar o mesmo teste contra a mesma amostra
real é o jeito mais direto de provar isso. Ver também [[tecnica-matriz-de-mutacao]] para a mesma
lógica de isolamento (copiar/rodar fora da árvore compartilhada).
