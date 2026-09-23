---
name: geracao-documento-exemplo-correcao-mapper-obrigatorio-2026-09-09
description: Correção do dono ao ADR de geração de documento de exemplo — exige mapper TCL/XSL/XSLT já existente, não gera para layout "solto"
metadata:
  type: project
---

Correção sobre [[geracao-documento-exemplo-2026-09-09]]: o dono esclareceu que gerar documento
de exemplo só faz sentido quando já existe mapper (TCL/XSL/XSLT) vinculado ao layout — serve pra
alimentar/testar esse mapper, não é amostra solta. `docs/architecture/adr-geracao-documento-exemplo-2026-09-09.md`
ganhou seção de correção (commit `1b98920`, branch `docs/adr-geracao-documento-exemplo`).

**Pré-condição do endpoint:** reaproveitar `MapperDatabaseService.GetBestMapperForLayoutGuidAsync`
(`Services/Database/MapperDatabaseService.cs:187`) — se retornar `null`, 404 explicativo, não
silenciar. Essa query já herda a landmine de `AllowedPackageGuids` vazio
([[lowcode-allowedpackageguids-empty-in-null-2026-08-15]]).

**Achado chave da reavaliação:** os 54 pares do held-out (#352) JÁ têm mapper por definição
(são pares schema-TCL+XSLT-alvo) — a pré-condição do dono não muda a viabilidade do backfill,
só confirma explicitamente o gate que [[repair-batch-convergencia-real-issue-352]] já achou por
outro caminho: falta é INSTÂNCIA real de entrada (2-4 de 54 casos), não mapper. Ainda faltam,
sem mudança: (1) coerência semântica do valor gerado, (2) cobertura Xml vs TextPositional dos
casos sem instância (não verificado).

**Decisão de fluxo:** exemplo gerado NÃO entra automático no `RepairOrchestrator`/
`RepairBatchRunner` — fica pra revisão humana antes de virar insumo de medição, mesmo princípio
de [[contrato-correcao-guiada-humano-2026-09-08]]. Fase 5 opt-in (`--allow-synthetic-instance`)
cogitada mas não comprometida.

**How to apply:** se alguém propuser conectar geração de exemplo ao backfill em lote sem
validação humana no meio, lembrar que o risco é contaminar a métrica de convergência com dado
semanticamente não confiável — reforçar o gate de revisão antes de aceitar.
