---
name: contrato-correcao-guiada-humano-2026-09-08
description: ADR de contrato do endpoint de correção guiada por humano (field-correction), pedido cross-repo React #232/#234
metadata:
  type: project
---

ADR entregue em `docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md`
(branch `docs/adr-contrato-correcao-humana`, não pusheada). Endpoint novo proposto
`POST /api/transformation/field-correction` — assíncrono (202, não re-executa síncrono),
autoria via `ICurrentUser` (padrão já usado em `MappingGovernanceController`), `documentId`
estável proposto como hash determinístico (não existia antes, `CorrelationId` é efêmero).

**Decisão central:** correção humana NÃO tem o mesmo critério de aceite "1:1 contra o
Sysmiddle" do [[fine-tuning-nichado-ollama-2026-09-02]]/loop automático — o usuário está por
definição discordando do oráculo Sysmiddle (ex. `<nNF>001</nNF>` vs `<nNF>1</nNF>`, nenhum
diff estrutural resolve isso). Reporte fica `pending` até curadoria humana aprovar antes de
virar exemplo de treino, pra não contaminar o dataset incremental (F3, issue #338,
`TrainingDataCaptureService`) com correções erradas do usuário.

**Why:** evitar que o pipeline de dataset incremental (que hoje só ingere convergências
automáticas confiáveis 1:1) misture dado não-verificado sem gate.

**How to apply:** se aparecer pedido de "aceitar correção humana automaticamente" no futuro,
apontar pra esta distinção — não é decisão técnica trivial, precisa de segundo oráculo ou
revisão humana explícita.

Recomendei ao `@lp-pm` 2 issues separadas (endpoint+persistência vs. fila de curadoria) —
não criei eu mesma.
