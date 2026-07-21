# Memória — lp-architect (Aria)

- [Infra da máquina dev (2026-07-18)](dev-machine-infra.md) — runner = serviço LocalSystem; deploy em `C:\inetpub\wwwroot\layoutparser`; elevação só via UAC (conta nddraiz); porta 5100.
- [Regressão de segurança no appsettings](security-regression-appsettings.md) — senha SQL ainda comitada; checklist do security.md desatualizado — verificar o arquivo, não o doc.
- [Reconciliação Trilha A/B (2026-07-16)](track-a-reconciliation.md) — Trilha A incompleta; branch nomeada estava vazia, trabalho real (só doc) ficou em `docs/track-a-a1-status`.
- [Hábito de auditoria de branch](branch-audit-habit.md) — sempre `git diff` completo, não `--stat`, antes de confiar que uma branch tem código.
- [Spec A2-A5 da Trilha A](track-a2-a5-spec.md) — contrato consolidado em §8 do plano multi-sessão, escrito pós-A1 pronto.
- [Incidente TLS no runner GitHub Actions (2026-07-20)](runner-tls-cert-incident.md) — cert expirado do lado do GitHub (`pipelines*.actions.githubusercontent.com`), não é a máquina; checar isso primeiro antes de investigar local.
- [Duplicação de pathway de transformação (2026-07-21)](transformation-pathway-duplication.md) — Pathway 1 (TransformationController, tem XSD, sem caller no front) vs Pathway 2 (TransformationExecutionController, sem XSD, é o que o front chama).
- [Aba XML Transformação Final já existe (2026-07-21)](frontend-transformation-tab-built.md) — AnalysisModeTabs+Tabs+XmlTransformationDisplay já implementados e wired no l-bottom-right; não é decisão em aberto.
- [ai/XslSynth isolado + overlap com Trilha A (2026-07-21)](xslsynth-trilha-a-overlap.md) — projeto standalone deliberado (doc de arquitetura confirma); é o alvo ativo da Trilha A (Lia) — sequenciar, não tratar como território livre.
- [Gap de segurança: subsistema Gemini/OpenAI (2026-07-21)](gemini-cloud-xsd-diagnosis-gap.md) — 4 call-sites + escopo de remoção (Tier 1/2) + achado incidental do RAGController; DI quebrado hoje, nada vaza na prática, é landmine.
- [Decisão: decomissionar Gemini/OpenAI + sem fine-tuning + hardware CPU-only (2026-07-21)](gemini-openai-decommission-decision.md) — Ollama 100% pro diagnóstico; síntese de dado é caso à parte (fork open-source OK, com ressalvas de licença/memorização); recomendação de modelo revisada pra CPU-only.
- [Máquina de dev é iGPU-only, sem GPU discreta (2026-07-21)](dev-machine-gpu-constraints.md) — achado original da máquina WSL; hardware relevante de produção agora é outro, ver `production-server-hardware.md`.
- [Visão expandida: diagnóstico fiscal, CFOP, AppConnector (2026-07-21)](ia-fiscal-diagnosis-vision.md) — FECHADA (5/5 perguntas respondidas); AppConnector é local (não repo externo); sidecar A6 corrigido; CHAVEACESSO=LinkMapping confirmado; CFOP 100% greenfield.
- [Dispatch consolidado da sessão inteira (2026-07-21)](../../../docs/architecture/ai-roadmap-dispatch.md) — `docs/architecture/ai-roadmap-dispatch.md`: ponto de entrada único pra Dex/Lia executarem tudo decidido nesta sessão (Gemini/OpenAI, pathway, diagnóstico XSD, síntese, CFOP, NT-pipeline).
- [Specs reais do servidor de produção (2026-07-21)](production-server-hardware.md) — `BRNDDAPPBLD01`, i7-4790 Haswell 2014, sem GPU, sem upgrade previsto; recomendação de modelo revisada pra 1-2B + medir de verdade.
- [SEFAZ: PL_009 já espelhado no GitHub (2026-07-21)](sefaz-xsd-schema-source.md) — `nfephp-org/sped-nfe` resolve o bloqueio do XSD antigo sem scraping; WebFetch na SEFAZ falhou (TLS, causa não confirmada).
