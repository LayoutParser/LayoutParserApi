# Memory Index — lp-backend-dev (Dex)

- [NuGet private feed 401](nuget-private-feed-401.md) — 401 no feed privado só no lado WSL; pelo Windows (powershell.exe) o TFS autentica. Se `dotnet`/`powershell.exe` não forem achados na Bash tool, usar caminho absoluto do .exe.
- [Remoção do subsistema de logging morto (2026-07-27)](dead-logging-subsystem-removal-2026-07-27.md) — lista de 11 arquivos do dispatch virou 14 por dependência transitiva (DataGenerationLogger/TextFileLoggerService).
- [Roadmap de IA 2026-07-21 — escopo Dex](ai-roadmap-2026-07-21-dex-scope.md) — o que foi feito (1.1/1.4/2.1/3.1/3.2/3.6) e o que fica bloqueado de propósito (1.2/1.3, 3.4/3.5) e por quê.
- [Generation services sem registro no DI](generation-services-unregistered-di.md) — não é só RAGController: GeminiAIService e DataGenerationController inteiro também quebram em runtime.
- [Ollama dev vs. BRNDDAPPBLD01](dev-ollama-vs-brnddappbld01-hardware.md) — Ollama local (i5-1135G7) não representa a produção (Haswell/AVX2/sem GPU); não confundir benchmark.
- [Dono do projeto e contexto de TCC](project-owner-and-tcc-context.md) — usuário é decisor final de arquitetura/domínio fiscal; projeto é TCC, exige rigor acadêmico (ex.: near-duplicate em dado sintético).
- [Logging unificado — implementação 2026-07-28](unified-logging-implementation-2026-07-28.md) — formato de linha real difere entre API (Serilog) e Lib/Decrypt (RollingFileLogger); fix de parse+log-spam no commit `975a84b`, teste contra arquivo real grande ainda pendente.
- [Harness isolado pra testar leitura de log](isolated-log-testing-harness-pattern.md) — nunca testar contra `Logging:File:Directory` de produção; padrão validado com console+FrameworkReference.
- [XmlAnalysisController DI fix (2026-07-29)](xmlanalysiscontroller-di-fix-2026-07-29.md) — GeminiAIService no construtor derrubava o controller inteiro; endpoint analyze-xsd-error-with-ai removido (substituto Ollama já existe).
- [Duas rotas VM→painel de métricas de IA](ai-metrics-duas-rotas-vm-para-painel.md) — POST ingest e ponte de cópia de log resolvem o MESMO bug; ativar as duas duplica cada geração e o dedup não colapsa (fusos diferentes).
- [Endpoint execute-candidates (Gap 1, 2026-07-28)](execute-candidates-endpoint-2026-07-28.md) — decisões de design não fechadas no contrato: 400 via consulta DB, timeout do conjunto = RunnerTimeoutSeconds*MaxConcurrentRunners, CandidateId/Score/Validation por pathway.
