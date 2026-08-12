# Memory Index — lp-devops (Gage)

- [Runner isolation rollout](runner-isolation-rollout.md) — ci-dev FAZ deploy (serviço nativo, 5100); criar vars/secrets DEV antes de push feat/**; rotação SQL BLOQUEADA (DBA); prod com paths-ignore.
- [gh CLI ausente](env-gh-cli-ausente.md) — sem gh na workstation; status do CI dev sai de `C:\actions-runner\_diag\Worker_*.log`; datar deploy de prod via `/api/logs?search=`.
- [Prod .42 sem acesso admin](prod-42-acesso-bloqueado.md) — SSH/WinRM/SMB/RPC todos negados no 172.25.32.42; desbloqueio = pub key no administrators_authorized_keys.
- [Topologia do job de métricas](metrics-job-topology-vm.md) — VM Ubuntu 172.25.32.31 roda Ollama E o cron do metrics-batch (sáb 00:00); ponte de log é necessária; cuidado com `logs/` vs `Logs/`.
- [Gemini/OpenAI decommission](gemini-decommission-secrets.md) — Gemini: revogar (não rotacionar), ação manual do usuário; SQL rotation segue à parte/bloqueada.
- [LayoutParserCypress bootstrap](layoutparser-cypress-bootstrap.md) — repo novo 2026-07-28, E2E NF-e x e-forms/Pollux, NÃO vendorizado de ndd-api-plataforma-cypress, sem push/remoto ainda; cypress.env.json real já preenchido.
- [GitHub protections pendentes](github-protections-pending.md) — merge-gate/environment existem no repo mas NÃO enforced (2026-08-11); required check, environment reviewer e branch protection da master são config de UI do dono; API_URL_PROD não existe.
