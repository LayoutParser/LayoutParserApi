---
name: dead-logging-subsystem-removal-2026-07-27
description: Remoção do subsistema ILoggingStrategy/ElasticSearch (nunca no DI) em 2026-07-27 — lista de 11 arquivos do dispatch cresceu para 14 por dependência transitiva não mapeada.
metadata:
  type: project
---

Executado em 2026-07-27 (commit `2e251c1`), decisão já fechada pela arquiteta
(Opção A): removida a cadeia morta `ILoggingStrategy` →
`LoggingStrategyFactory`/`ConsoleLoggingStrategy`/`FileLoggingStrategy`/
`ElasticSearchLoggingStrategy` → `ConfigurableLogger` → `ILoggerService` →
`ElasticSearchLoggerService`/`TextFileLoggerService`, mais os adapters órfãos
`TechLoggerAdapter`/`AuditLoggerAdapter`. Nunca esteve no DI do `Program.cs` —
Serilog + `TechLogger`/`AuditLogger` (em `Services/Implementations/`) são o
logging efetivo, e continuam intocados.

**Achado no double-check (não estava na lista de 11 do dispatch):**
`DataGenerationLogger`/`IDataGenerationLogger` (dependia de `ConfigurableLogger`
no construtor) e `TextFileLoggerService` (implementava `ILoggerService`) são
consumidores diretos da cadeia morta, e eles próprios nunca foram registrados
em lugar nenhum (nem `DataGenerationController`, que também usa DI quebrado —
ver [[generation-services-unregistered-di]] — referencia
`IDataGenerationLogger`; confirmado via grep). Sem removê-los junto, o
`dotnet build` quebrava (tipo referenciado não existe mais). Lista final: 14
arquivos deletados, não 11.

**Why:** investigações de "código morto" que só checam se algo está no
`Program.cs`/DI podem não pegar uma segunda camada de classes mortas que só
são consumidas pela primeira camada (não pelo runtime real). O grep de
segurança dupla pedido no protocolo (buscar cada nome fora de si mesmo antes
de apagar) pegou isso.

**How to apply:** em qualquer tarefa futura de "delete estes N arquivos, já
confirmado morto", rode o grep de confirmação pedido literalmente — não pule
achando que é redundante. Se aparecer uma classe fora da lista que referencia
algo que vai ser apagado, ela quase certamente também é morta (mesmo critério:
não registrada em `Program.cs`) e precisa entrar na remoção, senão o build
quebra. Reportar a expansão de escopo explicitamente em vez de silenciar.

Também limpos: `appsettings.json` (seção `ElasticSearch`, `Logging:Type`/`Txt` —
mantido `Logging:File`, sink Serilog real), `README.md` (linhas de config e
`user-secrets`/env var do Elastic) e `.claude/rules/security.md` (linha da
tabela de credenciais do Elastic passou de 🟡 "rever" para ✅ "removido —
nunca conectado ao pipeline real").
