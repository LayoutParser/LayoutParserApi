# Memory Index — lp-devops (Gage)

- [Runner isolation rollout](runner-isolation-rollout.md) — ci-dev FAZ deploy (serviço nativo, 5100); criar vars/secrets DEV antes de push feat/**; rotação SQL BLOQUEADA (DBA); prod com paths-ignore.
- [gh CLI ausente](env-gh-cli-ausente.md) — sem gh na workstation; status do CI dev sai de `C:\actions-runner\_diag\Worker_*.log`; datar deploy de prod via `/api/logs?search=`.
- [Prod .42 sem acesso admin](prod-42-acesso-bloqueado.md) — SSH/WinRM/SMB/RPC todos negados no 172.25.32.42; desbloqueio = pub key no administrators_authorized_keys.
- [Topologia do job de métricas](metrics-job-topology-vm.md) — VM Ubuntu 172.25.32.31 roda Ollama E o cron do metrics-batch (sáb 00:00); ponte de log é necessária; cuidado com `logs/` vs `Logs/`.
- [Gemini/OpenAI decommission](gemini-decommission-secrets.md) — Gemini: revogar (não rotacionar), ação manual do usuário; SQL rotation segue à parte/bloqueada.
- [LayoutParserCypress bootstrap](layoutparser-cypress-bootstrap.md) — repo novo 2026-07-28, E2E NF-e x e-forms/Pollux, NÃO vendorizado de ndd-api-plataforma-cypress, sem push/remoto ainda; cypress.env.json real já preenchido.
- [GitHub protections pendentes](github-protections-pending.md) — 2026-08-12: master e develop AGORA protegidas (PR+1 aprovação, sem push direto); GitHub nativo NÃO restringe origem do PR — "master só de develop" tem lacuna, falta workflow custom.
- [Rede loopback + ApiKeyGateFilter removido](rede-loopback-e-apikey-removido.md) — 2026-08-12: API_KEY_DEV/PROD limpos de ci-dev/deploy.yml; bind mudou de 0.0.0.0 para 127.0.0.1 via Kestrel__Endpoints__Http__Url no deploy.yml (canal que sobrevive à preservação do appsettings.json); assume BFF+API co-hospedados no .42, não 100% confirmado.
- [gh CLI não autenticado](gh-cli-nao-autenticado.md) — RESOLVIDO 2026-08-12: gh autenticado (elson-vinicius-lopes); PR #28 criada (feat/identidade-do-bff → develop); só ajustar PATH por sessão Bash.
