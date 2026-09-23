---
name: repair-batch-convergencia-real-issue-352
description: Implementação do --mode=repair-batch (issue #352) e o achado real que RepairOrchestrator.RunAsync não é chamável direto contra o dataset held-out.
metadata:
  type: project
---

Issue #352 (Fase B do plano de eval-benchmark, `docs/architecture/plano-eval-benchmark-ia-2026-09-08.md`)
implementada em `feat/metrica-convergencia-real-352` (commit c44884d, a partir de `develop`),
2026-09-08. Novo modo `--mode=repair-batch` em `ai/XslSynth/Metrics/RepairBatchRunner.cs`.

**Achado bloqueante investigado e contornado (não escondido):** `RepairOrchestrator.RunAsync`
(`ai/XslSynth.Core/Core/RepairOrchestrator.cs`) exige `MapperVo` estruturado (LinkMappings/Rules)
+ XML de INSTÂNCIA real de entrada — o dataset held-out (`dataset_pairs_filtered_v2.jsonl`) só
tem pares (schema TCL texto, XSLT-alvo texto), sem nenhum dos dois. Não dá para chamar a classe
como está. Solução: reaproveitar os MESMOS primitivos de decisão (`CanonicalDiffer`,
`XsdValidator`, `XsltApplier`, `OllamaXslSynthesizer.RepairFromDiffAsync` — este último não usa
`briefing.Mapper` de verdade, só `currentXsl`+`diffs`, então aceita um `MapperVo` vazio de
placeholder) num loop equivalente, com o candidato nascendo de geração direta via LLM (como o
`metrics-batch` já fazia), não de transpilação determinística de Rules.

**Cobertura real é limitada por DADO, não por código:** diff==0 real só é computável quando existe,
em `--instances`, um TXT real que estruturalmente case com o schema TCL do caso (via
`TclRootBuilder`, reaproveitado do `CandidateXmlFactory` mas SEM o filtro de elegibilidade
Pollux). Hoje isso cobre uma fração pequena dos 54 pares do held-out (majoritariamente NFe — ver
[[metrics-batch-mode-item1]] e achado de `CandidateXmlFactory`: só 2-4 de 54 são NFe+envio com
instância compatível). `RepairBatchRunner.Summarize` calcula a taxa sobre os casos MEDIDOS
(`InstanceMatched=true`), NUNCA sobre o total — casos sem instância aparecem como "sem instância"
no resumo, não contam nem a favor nem contra (evita falso-negativo sistemático). Ampliar a
cobertura exige mais TXT de instância real em `--instances`, tarefa separada de infraestrutura de
dado, não deste código.

**Testes:** `RepairBatchSummaryTests.cs` (6 testes, agregação pura sem I/O/Ollama) —
`XslSynth.Core.Tests` passou a referenciar `XslSynth.csproj` (antes só referenciava
`XslSynth.Core.csproj`) para acessar `RepairCaseResult`/`RepairBatchSummary`/`Summarize`
(todos tornados `public`, eram `internal`).

**Não validado nesta sessão:** rodada E2E real contra Ollama/VM (sem acesso de rede confirmado).
Reportado explicitamente no comentário da issue #352, não fingido como testado.

**How to apply:** ao revisitar este modo, lembrar que "taxa de convergência real" no relatório do
`repair-batch` é sempre uma taxa PARCIAL (sobre os casos com instância disponível) — nunca
reportar como "taxa do dataset inteiro" sem essa ressalva, mesmo erro de enquadramento que já
aconteceu com `XsdValido=null` no `metrics-batch` original ([[metrics-batch-mode-item1]]).
