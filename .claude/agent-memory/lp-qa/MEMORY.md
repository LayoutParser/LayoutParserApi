# Memory Index — lp-qa (Quinn)

- [Unified logging parse bug + log dir incident](unified-logging-parse-bug-and-log-dir-incident.md) — bug DateTimeStyles + incidente de log dir; RE-VALIDADO PASS (975a84b) com arquivos reais via harness isolado (nunca subir API no dir de produção).
- [LowCode-auto multi-candidate QA gate](lowcode-auto-multicandidate-qa-gate.md) — dedup/paralelismo OK; timeout/semáforo+entrega síncrona RE-VALIDADO PASS (bd8279c); achado à parte: branch XML omite transformationsStatus.
- [Fine-tuning POC Fase 1 dataset QA](finetuning-poc-fase1-dataset-qa.md) — 39 pares filtrados (NFe 31/MDFe 6/CTe 2); 11/11 amostras OK; CTe com amostra fina (só 2); dataset OK p/ Fase 2 RAG.
- [AI metrics Gap 3 QA gate](ai-metrics-gap3-qa-gate.md) — Endpoint 3 (cypress-result) FAIL 2026-07-30; 3 fragilidades estruturais: 2 Serilog no mesmo arquivo, log rotativo como banco, texto livre em parser por espaço.
- [Cypress alpha emissão normal spec](cypress-alpha-emissao-normal-spec.md) — spec escrita em LayoutParserCypress; ambos pathways (TCL/XSL e LowCode) bloqueados no dev workstation por arquivos que só existem em `C:\inetpub\wwwroot\layoutparser\` de produção.
