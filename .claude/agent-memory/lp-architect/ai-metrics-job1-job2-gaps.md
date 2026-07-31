---
name: ai-metrics-job1-job2-gaps
description: Achados estruturais do pipeline de métricas de IA (Job 1 metrics-batch -> Job 2 Cypress/Pollux) — desconexão de log Windows/VM, ausência de persistência de candidato e escopo real de 4/54 pares
metadata:
  type: project
---

Três gaps estruturais do encadeamento Job 1 (`ai/XslSynth --mode=metrics-batch`, cron sábado na VM
`172.25.32.31`) → Job 2 (Cypress vs Pollux), levantados em 2026-07-30. Especificação completa em
`docs/architecture/handoff-job2-cypress-batch.md`.

1. **O painel do Gap 3 nunca mostrou dado real.** A API lê o log em
   `Logging:File:Directory` = `C:\inetpub\wwwroot\layoutparser\api\logs\` (Windows); o Job 1 escreve em
   `~/layoutparser-ai-metrics/Logs/` (VM Linux). Arquivos distintos, máquinas distintas ⇒ as linhas
   `Geracao concluida.` nunca chegam ao `AiMetricsReaderService`, `GET /api/ai-metrics/generations`
   volta vazio e o merge do `POST /cypress-result` não casa com nada. O endpoint funciona; a
   integração não fecha. Solução recomendada: endpoint de ingestão de gerações na API (simétrico ao
   `cypress-result`), **não** montar SMB nem copiar log.

2. **O Job 1 não persiste candidato algum** — gera o XSLT, valida em memória (`OutputValidator`) e
   descarta. Não há run dir, manifesto nem arquivo de saída.

3. **Escopo real do Job 2 é N=1–4, não 54.** O dataset é `TCL(schema) → XSLT`; o Pollux consome XML de
   NF-e. Dos 54 pares, só 4 (`NFe…EnvioNFe…`) produzem raiz `<NFe>`; o resto é retorno SEFAZ→ERP,
   consulta, CT-e/MDF-e. E o único TXT de instância existente (`nfe-emissao-normal.mq_series.txt`,
   layout `LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe`, blocos `LINHA001`) **não casa** com o TCL do dataset
   (`LINE identifier="A"`). Sintetizar instância foi rejeitado: a SEFAZ-fake rejeita por chave/DV/CNPJ,
   medindo o gerador de dados e não o XSLT.

**Why:** o plano `plano-metricas-ia-servidor-producao.md` §7.5 assumia que bastava a spec Cypress
iterar sobre "os N candidatos gerados pelo Job 1" — nada disso existia; o encadeamento estava a três
entregas de distância, não uma.

**How to apply:** antes de propor qualquer evolução do painel de métricas de IA ou do Job 2, verifique
se (1) e (2) já foram resolvidos — sem eles, qualquer métrica de aceitação Pollux continua invisível no
painel. Não prometa cobertura dos 54 pares; o gargalo é TXT de instância real, não capacidade do LLM.
Ver também [[transformation-pathway-duplication]] (o Job 2 usa o Pathway 2, canônico).

**Correlato:** os scripts de produção da VM (`run-metrics-batch.sh`, `enable/disable-metrics-job.sh`)
**não são versionados** — a string `run-metrics-batch` só aparece na documentação. A VM é a única cópia.
