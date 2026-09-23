---
name: auditoria-17-issues-2026-09-03
description: Resultado da execução da auditoria de 17 issues OPEN (docs/architecture/auditoria-17-issues-backlog-2026-09-03.md) — 4 fechadas, 3 comentadas como bloqueadas, 2 devolvidas ao dono.
metadata:
  type: project
---

Executei as ações da auditoria de código de 2026-09-03 (feita por outro agente/sessão em
`docs/architecture/auditoria-17-issues-backlog-2026-09-03.md`).

**Fechadas (evidência de código, sem ambiguidade):**
- #96 — FindXslFile teve sourceType/targetType removidos (TransformationPipelineService.cs:424).
- #103 — guarda-chuva, 3 sub-issues (#229/#230/#231) todas CLOSED.
- #108 — investigação entregue (mecanismos A1/A2/B1/B2 avaliados); execução real fica com #110/#112, que continuam abertas.
- #137 — guarda-chuva, 4 sub-issues (#138-#141) todas CLOSED, fieldMappings confirmado em uso.

**Comentadas como bloqueadas (sem fechar):**
- #112 — bloqueada por #110 (dry-run de config drift ainda não executado contra produção).
- #196 — bloqueada esperando correlationId real do dono (LINHA006 .mqseries).
- #216 — bloqueada por #213 no doc original, mas **checar `gh issue view 213` mudou o quadro**:
  #213 já estava CLOSED no momento da execução (não confirmado assim na auditoria original). Comentei
  sinalizando isso e pedindo confirmação ao dono se o bloqueio ainda vale.

**Devolvidas à decisão do dono (não fechei nem priorizei):**
- #97 — provavelmente superada por #102 (CLOSED), mas falta confirmar TTL/retenção; pedi decisão de escopo.
- #151 — bloqueio por #139/#140 removido (ambas CLOSED); comentei tirando o rótulo de bloqueio, sem fechar.

**Padrão a repetir:** antes de comentar/fechar uma issue bloqueada por outra issue-pai fora do
escopo do doc de auditoria (caso #216/#213), sempre rodar `gh issue view <pai> --json state` —
o estado pode ter mudado desde que o doc de auditoria foi escrito. Ver também
[[project_board-sync-2026-08-28]] sobre closing keyword não atravessar repositório (padrão
correlato: nunca confiar cegamente em status registrado em doc estático).
