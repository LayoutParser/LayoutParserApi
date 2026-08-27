# Memory Index — lp-qa (Quinn)

- [Unified logging parse bug + log dir incident](unified-logging-parse-bug-and-log-dir-incident.md) — bug DateTimeStyles + incidente de log dir; RE-VALIDADO PASS (975a84b) com arquivos reais via harness isolado (nunca subir API no dir de produção).
- [LowCode-auto multi-candidate QA gate](lowcode-auto-multicandidate-qa-gate.md) — dedup/paralelismo OK; timeout/semáforo+entrega síncrona RE-VALIDADO PASS (bd8279c); achado à parte: branch XML omite transformationsStatus.
- [Fine-tuning POC Fase 1 dataset QA](finetuning-poc-fase1-dataset-qa.md) — 39 pares filtrados (NFe 31/MDFe 6/CTe 2); 11/11 amostras OK; CTe com amostra fina (só 2); dataset OK p/ Fase 2 RAG.
- [AI metrics Gap 3 QA gate](ai-metrics-gap3-qa-gate.md) — 6 bloqueios FECHADOS (9e48650) + hardening CONCERNS (e6df0b7); em aberto: duas pontes ativas contam cada geração 2x (54 vira 108, aprovação 100% vira 50%).
- [Técnica: matriz de mutação](tecnica-matriz-de-mutacao.md) — julgue suíte reintroduzindo bugs numa cópia via `git archive` no scratchpad; nunca mutar a árvore compartilhada.
- [InformacoesParaEDI OccurrenceCount fix QA gate](informacoesparaedi-occurrencecount-fix-qa-gate.md) — PASS a330af2 validado c/ amostra real; baseline 704 era stale, correto é 705 (pré-existente, não regressão).
- [Cypress alpha emissão normal spec](cypress-alpha-emissao-normal-spec.md) — spec escrita em LayoutParserCypress; ambos pathways (TCL/XSL e LowCode) bloqueados no dev workstation por arquivos que só existem em `C:\inetpub\wwwroot\layoutparser\` de produção.
