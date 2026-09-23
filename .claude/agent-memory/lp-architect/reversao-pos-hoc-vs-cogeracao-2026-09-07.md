---
name: reversao-pos-hoc-vs-cogeracao-2026-09-07
description: Recomendação de arquitetura para issue #151 (reconstrução reversa XML→TXT) — híbrido entre reversão estrutural pós-hoc e co-geração do reverso na autoria
metadata:
  type: project
---

Issue #151 tinha duas estratégias concorrentes propostas pelo dono em 2026-09-07: Opção 1
(reversão pós-hoc — o que Fases A-D do design + spike A/B já mediam) vs. Opção 2 (co-geração:
a IA que gera o mapeamento Forward também produz o Reverse no mesmo ato, enquanto ainda tem
acesso ao TXT original). Recomendação registrada em
`docs/architecture/design-reconstrucao-reversa-xml-txt-2026-09-03.md` §7 (commit `2075baf`,
branch `feat/spike-reconstrucao-reversa-151`) + comentário na issue #151.

**Achado que reabre o número do spike A/B:** o catálogo de 173 funções Sysmiddle medido em
`ai/XslSynth.Contracts` (13,3% reversível) é de um subsistema diferente do que gera TCL/XSLT
publicado de verdade. O pipeline real de autoria (Slice 3→5, issue #231,
`Services/Fiscal/MappingDraftRuleTranspiler.cs`) só tem 5 operações: copy/concat/lookup/
conditional/constant (`SupportedOperations`, linha 44-47) — catálogo de reversibilidade
correto a curar é esse, bem menor, teto teórico 20-40% (já apontado no refinamento
`f102b21`, confirmado aqui como o alvo certo).

**Por quê:** `MappingDraftRule` (Slice 3) já é estruturado (`SourceRefs`/`TargetRefs`/
`Operation`/JSON) antes de virar texto — a compilação (`MappingDraftRuleTranspiler.ToXslt`/
`ToTcl`) é determinística, sem IA. O ponto de alavancagem da Opção 2 é o passo 1
(`MappingSuggestionService.EnqueueAsync`, chamada Ollama), não o passo de compilação.

**Opção 2 não elimina irreversibilidade matemática** — `constant` continua lossy por
definição (sem origem), mesma lógica de `CalculateVerifierDigit`. O que ela faz é reter o
dado de origem no momento em que existe, em vez de inferir depois. É trade-off real, não
"resolve reversibilidade 100%" — comunicar isso ao dono sempre que a opção for discutida de
novo, pra não prometer mais do que o desenho entrega.

**Recomendação: híbrido.** (0) catálogo de reversibilidade das 5 operações — pré-requisito
comum, pequeno; (1) Opção 2 aditiva pra mappings novos (schema + prompt/parsing +
2 métodos novos em `MappingDraftRuleTranspiler` + adapters); (2) Opção 1/best-effort
(Fases A-D já desenhadas) como fallback só pro corpus já existente, que não pode ganhar
retroativamente a contraparte co-gerada.

**Descoberta lateral não relacionada à decisão:** o design original
(`design-reconstrucao-reversa-xml-txt-2026-09-03.md`) só existia em
`hotfix/build-quebrado-aiusersessionstore-testes-develop` — nunca chegou a `develop`/
`master`/à branch do spike (`feat/spike-reconstrucao-reversa-151`). Restaurado nesta sessão
como base do §7. Vale um alerta a `@lp-devops`/dono sobre esse doc estar "preso" numa branch
hotfix — mesma classe de problema já registrado em `[[track-a-reconciliation]]` (branch
nomeada vazia / trabalho real em lugar inesperado), mas aqui é doc "esquecido" numa hotfix
que não foi mergeada, não uma branch vazia.
