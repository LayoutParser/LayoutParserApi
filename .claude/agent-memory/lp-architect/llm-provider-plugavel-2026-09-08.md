---
name: llm-provider-plugavel-2026-09-08
description: ADR de provider de LLM plugável (Ollama default + nuvem opcional gated por DataSensitivity); 2 dos 5 call-sites de LLM têm proveniência ambígua real/sintético e bloqueiam nuvem até F2.
metadata:
  type: project
---

Pedido do dono 2026-09-08: LLM plugável (Anthropic/OpenAI/Kimi2 além de Ollama), com restrição
não-negociável: nuvem só com dado seguro (sintético/anonimizado/teste), nunca documento fiscal
real. ADR completo em `docs/architecture/adr-llm-provider-plugavel-2026-09-08.md`.

**Achado central:** hoje NENHUM dos 5 call-sites de LLM na API está pronto pra nuvem sem
trabalho extra. 3 são claramente REAL (`OllamaValidationDiagnosticService`,
`OllamaXslSynthesizer`/`RepairOrchestrator` em produção e no batch offline `MetricsBatchRunner`
— mesmo dado, reprocessado offline não fica mais seguro). 2 são AMBÍGUOS e tratados como REAL
até prova em contrário: `MappingSuggestionService` (artefato do draft pode ser amostra real
anexada pelo analista, sem campo de proveniência) e `SyntheticDataGeneratorService` (saída é
sintética por design, mas o PROMPT pode embutir planilha real do analista como referência de
formato — dado real pode vazar no input mesmo com output rotulado sintético).

**Mecanismo de bloqueio técnico (não só doc):** `DataSensitivity` enum obrigatório em todo
`LlmRequest` (RealFiscalDocument vs SyntheticOrAnonymized), fixado no código-fonte de cada
call-site REAL (não configurável via appsettings — fricção proposital, evita reclassificação
silenciosa por erro de config). `LlmProviderResolver.Resolve()` central lança exceção (não
degrada silenciosamente) se sensitivity=Real e provider.Locality=Cloud.

**Abstração:** `ILlmProvider` (`Services/Llm/`) segue o mesmo espírito de `IXslSynthesizer`
(`ai/XslSynth.Core/Synthesis/IXslSynthesizer.cs`) — já existe precedente de boa forma no
projeto. Não restaurar código dos providers Gemini/OpenAI removidos no decommission de
2026-07-21 — reimplementar do zero contra o contrato novo, com o gate desde o primeiro commit.
`ai/XslSynth.Core` é standalone (ver [[xslsynth-trilha-a-overlap]]) — F1 não mexe nele.

**Fases:** F1 (abstração, só Ollama, refactor baixo risco) → F2 (fechar gap de proveniência nos
2 call-sites ambíguos — PRÉ-REQUISITO real, não cosmético) → F3 (primeiro provider de nuvem,
só nos fluxos que F2 comprovou seguros) → F4 (providers adicionais). Nuvem em call-site REAL
fica fora de escopo permanente, só reabre com decisão explícita do dono.

Fine-tuning (`layoutparser-sysmiddle-dsl:1.5b`, PR #335, ver
[[fine-tuning-nichado-ollama-2026-09-02]]) continua paralelo — roda dentro do `OllamaLlmProvider`
default, não é substituído pela abstração de provider.

## How to apply

Se pedirem para priorizar/implementar F3 antes de F2 estar feito, sinalizar que o gap de
proveniência não é opcional — é o que decide se "sintético" é confiável ou só um rótulo. Ao
recomendar issue, não recomendar F3/F4 até o dono escolher provedor E confirmar aceitar F2
como pré-requisito.
