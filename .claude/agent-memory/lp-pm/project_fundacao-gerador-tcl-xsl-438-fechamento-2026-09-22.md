---
name: fundacao-gerador-tcl-xsl-438-fechamento-2026-09-22
description: Fechamento da #438 (fundação gerador TCL/XSL/XSLT) e abertura de 3 issues filhas de pendência real
metadata:
  type: project
---

Issue #438 (fundação do gerador TCL/XSL/XSLT: fix MQSeries + experimento RAG+Ollama + catálogo
Neogrid) foi fechada em 2026-09-22 a pedido do dono, depois de confirmado que sua fundação já
tinha sido entregue (PRs #434, #435, #437, #433/#439, #441, #448, #459 — casca do documento +
regra `xJust`, `GeneratorVersion="2"`).

3 issues filhas abertas para as pendências reais que ficaram de fora, todas `Ref #438` (sem
`Closes`, já que #438 foi fechada por mim, não por elas), adicionadas ao Project #2 com
Status=Todo:

- **#472** — Expandir geração RAG+Ollama para CT-e/MDF-e/NFS-e (hoje só NF-e/Inutilização foi
  validada ponta a ponta). Tamanho G. Dono natural `@lp-parser-llm`.
- **#473** — Job periódico de geração automática de TCL/XSLT (fase 2 do trigger lazy existente,
  #441). Concorrência baixa (1-2) porque Ollama de produção é CPU-only. Tamanho M.
- **#474** — Decidir e provisionar local permanente do corpus de referência Neogrid (hoje só
  existe localmente na máquina de quem trabalhou, `ReferenceExamples:BasePath` vazio em
  produção). É decisão do dono, não implementação. Tamanho P (decisão) + esforço variável depois.

**Pendência que NÃO virou issue:** estender `MappingReleaseArtifactSource` com valor
`Experimental` — já resolvida pelo ADR de unificação (`docs/architecture/
adr-unificacao-generated-artifact-mapping-release.md`, opção (b) view agregada, implementada em
#448). Confirmei lendo o ADR antes de decidir não abrir issue — a tarefa avisava para checar,
não assumir.

**Nota de processo:** o texto da tarefa citava um ADR
(`adr-geracao-automatica-gabarito-sysmiddle.md`) que **não existe** no repo — o mais próximo é
`adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md` (ref #151, sobre seed de
reaproveitamento de XSLT, não sobre o "job periódico varrendo catálogo com concorrência baixa"
descrito na tarefa). Não citei esse ADR na #473 porque o conteúdo não bate — escrevi a issue
com base só no que está confirmado no código/PRs (trigger lazy em #441, `GeneratorVersion` em
#459) e nas restrições de CPU-only já documentadas em
`adr-fine-tuning-nichado-ollama-2026-09-02.md`. Vale desconfiar de referências a arquivo dadas
em instruções de tarefa sem verificar que existem primeiro.

**Cuidado técnico:** ao usar `gh issue create --body "..."` com backticks dentro de string bash
com aspas duplas, o shell expande `` `comando` `` como substituição de comando (não como
markdown) — aconteceu com `` `MetricsBatchRunner` `` na #473, que sumiu do corpo até eu
recriar via heredoc (`<<'EOF'`, aspas simples no delimitador, que desabilita expansão). Usar
sempre heredoc com delimitador entre aspas simples para corpos de issue com backticks/code
spans, nunca `--body "..."` inline.
