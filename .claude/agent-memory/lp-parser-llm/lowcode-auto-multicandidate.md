---
name: lowcode-auto-multicandidate
description: Pathway LowCode-auto (2026-07-28) passou a rodar N>1 mapeadores plausíveis em paralelo em vez de colapsar sempre pra 1; N=4, critério de "genuinamente plausível" = MapperGuid distinto.
metadata:
  type: project
---

**Fato:** `LowCodeAutoTransformationService.TransformAndPersistAsync` (pathway LowCode-auto,
disparado fire-and-forget por `ParseController.Upload` só quando `detectedType == "mqseries"`)
sempre colapsava a lista de candidatos de `MapperDatabaseService.GetBestMapperForLayoutGuidAsync`
pra 1 único mapper. Adicionei em 2026-07-28 (commit `0e5bb22`, repo Api, branch `develop`)
`GetRankedMapperCandidatesForLayoutGuidAsync` — MESMA prioridade de sempre (input match >
target match > mais recente, sem scoring novo), mas retornando a lista ranqueada inteira,
deduplicada por `MapperGuid`.

**Critério de "genuinamente plausível":** distinto **MapperGuid** — não linha distinta.
Múltiplas linhas de `tbMapper` com o MESMO `MapperGuid` (ex.: histórico de updates) colapsam
pra `Count==1` depois do dedup, e o pathway continua no caminho de hoje
(`TransformSingleAndPersistAsync`, byte-a-byte igual, zero overhead). Só quando há 2+
`MapperGuid` distintos batendo no mesmo `layoutGuid` (ex.: variantes fiscais como
ICMS10/ICMS40 de clientes diferentes — ver [[multi-client-mappers]]) é que entra o caminho
multi-candidato (`TransformMultiCandidateAndPersistAsync`).

**N escolhido = 4** (config `LowCode:MultiCandidateTopN`, `LowCodeRunnerOptions.MultiCandidateTopN`,
default 4). Justificativa: o inventário real de variantes por cliente na instância FiatMQ (ver
[[multi-client-mappers]]) mostra tipicamente 2-5 variantes fiscais distintas por família de
mapper (ICMS10/40, IPITrib, PISNT/Outr) — 4 cobre a maioria sem rodar o runner low-code (processo
externo, ~0.5-1s de bootstrap cada) um número excessivo de vezes em paralelo.

**Shape persistido (sem consumidor hoje — só grava, nada no repo lê `MLData/LowCodeTransformations`
ainda):** quando N==1, artefatos idênticos a antes (`{base}.input.txt` + `{base}.lowcode.xml` +
`{base}.meta.json` com um único mapper). Quando N>1: 1 input compartilhado + 1
`{base}.meta.json` com `multiCandidate: true` e array `candidates[]` (mapperGuid, mapperName,
targetLayoutGuid, packageGuid, success, outputFile, outputLength, errorMessage) + 1 arquivo
`{base}.cand{i}_{mapperGuid}.lowcode.xml` por candidato que teve sucesso.

**Sem validação XSD neste pathway:** `AutomatedTransformationTestService`/`XsdValidationService`
não estão cabeados aqui (só existem em outro loop, o de aprendizado/RAG). O indicador de
"validade" por candidato é só sucesso/falha da execução do runner low-code — não inventei
validação nova, conforme instrução explícita da tarefa.

**Why:** decisão fechada pela arquiteta (Aria) — não redesenhar, só implementar. A parte de
julgamento que coube a mim (Lia) foi justamente o critério de "genuinamente plausível" (dedup
por MapperGuid) e o valor de N, porque a arquitetura não especificou isso.

**How to apply:** se no futuro alguém for CONSUMIR esse array de candidatos (front-end, RAG,
ou um novo endpoint síncrono), o shape já está definido acima — não reinvente. Se pedirem pra
adicionar validação XSD real a esse pathway, isso é trabalho novo (ligar
`AutomatedTransformationTestService` aqui), não algo que já existe e só ficou "esquecido".
Escopo desta mudança foi estritamente o pathway LowCode-auto — os pathways Legado
(`MapperTransformationService`) e Canônico (`TransformationPipelineService`) não foram tocados.
