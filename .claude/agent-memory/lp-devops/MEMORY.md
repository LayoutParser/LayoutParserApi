# Memory Index — lp-devops (Gage)

- [Runner isolation rollout](runner-isolation-rollout.md) — ci-dev FAZ deploy (serviço nativo, 5100); criar vars/secrets DEV antes de push feat/**; rotação SQL BLOQUEADA (DBA); prod com paths-ignore.
- [gh CLI ausente](env-gh-cli-ausente.md) — sem gh na workstation; status do CI dev sai de `C:\actions-runner\_diag\Worker_*.log`; datar deploy de prod via `/api/logs?search=`.
- [Prod .42 sem acesso admin](prod-42-acesso-bloqueado.md) — SSH/WinRM/SMB/RPC todos negados no 172.25.32.42; desbloqueio = pub key no administrators_authorized_keys.
- [Topologia do job de métricas](metrics-job-topology-vm.md) — VM Ubuntu (IP mudou por DHCP, ver [[vm-windows-connectivity-diagnostico-2026-08-13]] pro atual) roda Ollama E o cron do metrics-batch (sáb 00:00); ponte de log é necessária; cuidado com `logs/` vs `Logs/`.
- [Gemini/OpenAI decommission](gemini-decommission-secrets.md) — Gemini: revogar (não rotacionar), ação manual do usuário; SQL rotation segue à parte/bloqueada.
- [LayoutParserCypress bootstrap](layoutparser-cypress-bootstrap.md) — repo novo 2026-07-28, E2E NF-e x e-forms/Pollux, NÃO vendorizado de ndd-api-plataforma-cypress, sem push/remoto ainda; cypress.env.json real já preenchido.
- [GitHub protections pendentes](github-protections-pending.md) — 2026-08-12: proteção aplicada e depois PERDIDA (repos ficaram privados, plano free não cobre proteção avançada); dono optou por não assinar Pro; enforcement agora é só convenção.
- [Rede loopback + ApiKeyGateFilter removido](rede-loopback-e-apikey-removido.md) — 2026-08-12: API_KEY_DEV/PROD limpos de ci-dev/deploy.yml; bind mudou de 0.0.0.0 para 127.0.0.1 via Kestrel__Endpoints__Http__Url no deploy.yml (canal que sobrevive à preservação do appsettings.json); assume BFF+API co-hospedados no .42, não 100% confirmado.
- [gh CLI não autenticado](gh-cli-nao-autenticado.md) — RESOLVIDO 2026-08-12: gh autenticado (elson-vinicius-lopes); PR #28 criada (feat/identidade-do-bff → develop); só ajustar PATH por sessão Bash.
- [git fetch/pull trava (GCM)](git-fetch-hang-gcm-workaround.md) — HTTPS fetch/pull trava por prompt do Credential Manager mesmo com gh autenticado; workaround: `git -c http.extraHeader=...` com `gh auth token`.
- [VM Windows conectividade RESOLVIDO](vm-windows-connectivity-diagnostico-2026-08-13.md) — causa raiz era bridge no adaptador Hyper-V errado (não DHCP/ufw); IP atual `172.25.32.5`; `Ollama:Url` prod não verificado nesta sessão.
- [PR #118 duplicata fechada](pr-118-duplicata-fechada.md) — antes de mergear fix/* contra master, checar se já existe PR irmã do mesmo branch já mergeada em develop (`gh pr list --head`); se sim, fechar sem merge.
- [Purga de histórico git 2026-08-15](git-history-purge-2026-08-15.md) — filter-repo purgou senha SQL de todo o histórico; repos ficaram privados; instrução de "coordenador" pra reverter a público foi RECUSADA sem confirmação direta do dono.
- [PR #123 já mergeada](pr-123-ja-mergeada-hardening-secrets.md) — chore/security-hardening-secrets já publicada/mergeada antes; checar merge-base antes de tentar recriar PR de branch pronta.
