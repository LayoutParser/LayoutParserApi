# Memory Index — lp-devops (Gage)

- [Runner isolation rollout](runner-isolation-rollout.md) — ci-dev FAZ deploy (serviço nativo, 5100); criar vars/secrets DEV antes de push feat/**; rotação SQL BLOQUEADA (DBA); prod com paths-ignore.
- [gh CLI ausente](env-gh-cli-ausente.md) — sem gh na workstation; status do CI dev sai de `C:\actions-runner\_diag\Worker_*.log`; prod só via browser.
- [Topologia do job de métricas](metrics-job-topology-vm.md) — VM Ubuntu 172.25.32.31 roda Ollama E o cron do metrics-batch (sáb 00:00); ponte de log é necessária; cuidado com `logs/` vs `Logs/`.
- [Gemini/OpenAI decommission](gemini-decommission-secrets.md) — Gemini: revogar (não rotacionar), ação manual do usuário; SQL rotation segue à parte/bloqueada.
- [LayoutParserCypress bootstrap](layoutparser-cypress-bootstrap.md) — repo novo 2026-07-28, E2E NF-e x e-forms/Pollux, NÃO vendorizado de ndd-api-plataforma-cypress, sem push/remoto ainda; cypress.env.json real já preenchido.
