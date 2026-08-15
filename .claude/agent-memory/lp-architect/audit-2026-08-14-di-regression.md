---
name: audit-2026-08-14-di-regression
description: Auditoria review-arch achou DataGenerationController quebrado de novo (regressão silenciosa da issue #33) e AiCandidateStore leak (issue #51) fechada com fix no subsistema errado
metadata:
  type: project
---

Auditoria ampla (`review-arch`, 2026-08-14) em `docs/architecture/auditoria-gates-bugs-2026-08-14.md`
achou dois achados críticos que passaram por issues JÁ FECHADAS:

1. **DataGenerationController regrediu.** Issue #33 (fechada 2026-08-13) registrou os serviços de
   Generation no DI. Um commit posterior (`9e52791`, merge `612a5a3`, remoção do Pathway 1) apagou
   o bloco inteiro como dano colateral de resolução de conflito. Confirmado em `HEAD=1dc58f2`: o
   bloco não existe mais em `Program.cs`, o controller ainda injeta o serviço → quebra em runtime.
   Causa raiz do porquê o CI não pegou: `DataGenerationControllerDiTests.cs` testa um
   `ServiceCollection` próprio com os registros **copiados manualmente**, nunca o `Program.cs` real
   — um padrão de teste que pode voltar a falhar da mesma forma para qualquer outro serviço.

2. **AiCandidateStore leak (issue #51) fechado errado.** O comentário de fechamento cita fix em
   `ai/XslSynth/Metrics/RunManifest.cs` (retenção do Job 1 de métricas) — subsistema DIFERENTE do
   `Services/Transformation/Ai/AiCandidateStore.cs` que a issue #51 realmente descrevia. Confirmado:
   `AiCandidateStore.cs` ainda não tem TTL/cleanup nenhum, leak segue presente.

**Why:** ambos são casos de "issue fechada ≠ problema resolvido" — vale, ao auditar, sempre
verificar se o commit/arquivo citado no fechamento bate com o arquivo que a issue realmente
descrevia, não só confiar no status CLOSED.

**How to apply:** ao revisar arquitetura/gates no futuro, tratar toda issue fechada citando fix
"em outro lugar" (nome de arquivo diferente do mencionado no corpo da issue) como suspeita até
verificar o código atual. Padrão de teste "DI isolado copiado manualmente" (como
`DataGenerationControllerDiTests`) é um anti-padrão a sinalizar sempre que aparecer de novo —
recomendação dada: smoke test que resolve TODOS os controllers a partir do `Program.cs` real.

Related: [[gemini-openai-decommission-decision]]
