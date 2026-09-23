---
name: backfill-catalogo-nao-viavel-2026-09-08
description: Backfill em lote de convergência TCL/XSLT sobre todo o catálogo Sysmiddle não é viável hoje — corpus real de instância insuficiente, não é problema de CPU/escala
metadata:
  type: project
---

ADR: `docs/architecture/adr-backfill-catalogo-e-retraining-automatizado-2026-09-08.md`
(branch `docs/adr-backfill-retraining-automatizado`, comentado na issue #151). Estende
[[adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08]] (F1/F2/F3 já em produção).

O dono pediu duas coisas em 2026-09-08: (1) rodar o loop de convergência proativamente sobre
todo o catálogo já cadastrado (não só reativamente, quando documento real chega pra parse), e
(2) destravar F4 (retraining automatizado), que o ADR anterior deixou fora de escopo.

**F4 (retraining): viável**, desenho completo no ADR — gatilho por volume acumulado (F3) + teto
de agenda (90 dias), exclusão mútua obrigatória com o Ollama de inferência (mesma VM
172.25.32.5, `train_lora.py` ~40h em CPU), validação held-out antes de promover modelo novo.

**Backfill em lote: NÃO viável hoje.** Não é limite de CPU/escala — o catálogo real é pequeno
(`tbLayout` já é `TOP(200) WHERE ProjectId=2`, achado no código, não estimativa). O bloqueio real
é ausência de corpus de instância real por layout: reaproveitei o achado já existente de
[[ai-metrics-job1-job2-gaps]] (Gap 3, 2026-07-30) — de 54 pares do dataset, só 4 produzem raiz
`<NFe>` (candidatos a ter instância), e o único TXT de instância real conhecido
(`nfe-emissao-normal.mq_series.txt`, blocos `LINHA001`) não bate com o schema TCL do dataset
(`LINE identifier="A"`). Sintetizar instância já foi rejeitado antes (Pollux rejeita por
chave/DV/CNPJ — mede o gerador, não o XSLT). Recomendação: piloto de 1 layout com corpus real,
não backfill em escala; F3 (captura incremental já em produção) é o mecanismo certo de
crescimento orgânico do corpus.

**Achado incidental de segurança/escopo:** `MapperDatabaseService.GetAllMappersAsync`
(`Services/Database/MapperDatabaseService.cs:41`) lê `tbMapper` **sem `WHERE ProjectId`** — no
uso atual (warmup de cache local, read-only) é inofensivo, mas se alguém reaproveitar esse método
como base de enumeração para uma feature nova (backfill ou qualquer outra), o resultado inclui
mappers de outros times na credencial SQL compartilhada (`ConnectUS_Macgyver`, ~231.890 times).
Existe método já filtrado por `ProjectId`/`AllowedPackageGuids` no mesmo arquivo (~linha 284) —
usar esse, nunca o `GetAllMappersAsync` sem filtro, para qualquer enumeração nova.

**Why:** decisão do dono em 2026-09-08 de ampliar a linha de trabalho da #151 pra backfill
proativo + retraining automatizado. O ADR responde as duas com honestidade separada — não força
uma resposta otimista pro backfill só porque F4 é viável.

**How to apply:** se alguém propuser "gerar mais dado de treino rodando o catálogo inteiro", checar
primeiro se o corpus real por layout mudou desde 2026-09-08 (mais TXTs de instância disponíveis)
antes de reabrir essa recomendação — a resposta "não viável" é condicionada a corpus, não
definitiva. Ver também [[transformation-pathway-duplication]] e [[fine-tuning-nichado-ollama-2026-09-02]].
