---
name: project-execute-candidates-cnhi-gap
description: Issues #38-#40 formalizam o gap de execute-candidates para o layout CNHI ENVNFe, origem no diagnóstico do @lp-backend-dev
metadata:
  type: project
---

`POST /api/transformationexecution/execute-candidates` devolve `candidates: []` para `LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe`. `@lp-backend-dev` mapeou 3 causas independentes, nenhuma corrigível só com código deste repo, e viraram issues:
- #38 — investigação: `AllowedPackageGuids` pode excluir o Package real (precisa query SQL em produção, dono `@lp-devops` + dono do projeto).
- #39 — tech-debt: pathway tcl-xsl nunca resolve o MAP (convenção de nome/pasta divergente do dump real; pode ser legado morto pra todos os layouts, não só CNHI).
- #40 — story: pathway de geração via IA (Ollama/RAG `ai/XslSynth`) nunca foi integrado ao endpoint `execute-candidates` — só existe como CLI offline.

**Why:** nenhuma das 3 causas tem fix isolado óbvio; cada uma tem dono natural diferente (`@lp-devops`, `@lp-backend-dev`+`@lp-architect`, `@lp-architect`+`@lp-parser-llm`), por isso viraram issues separadas em vez de um item único.

**How to apply:** se o usuário voltar falando de "CNHI", "execute-candidates vazio" ou "pathway de IA no endpoint", essas 3 issues são o backlog já existente — checar status antes de abrir novas. Origem completa: `.claude/agent-memory/lp-backend-dev/execute-candidates-ausencia-total-para-cnhi-envnfe.md`.

Related: [[reference-gh-cli-setup]]
