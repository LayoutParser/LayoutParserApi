---
name: isolated-log-testing-harness-pattern
description: Padrão validado para testar serviços que leem arquivos de log (UnifiedLogReaderService) sem subir a API real nem tocar no diretório de log de produção — evita repetir o incidente de 2026-07-28.
metadata:
  type: feedback
---

Nunca testar código que lê `Logging:File:Directory` subindo a API real
(`dotnet run`) contra o `appsettings.json` base sem antes isolar esse diretório.

**Why:** em 2026-07-28 o QA (Quinn) subiu a API real e chamou `GET api/logs`
só duas vezes pra validar o endpoint de logging unificado. Um bug de parse
(`DateTimeStyles.RoundtripKind | AssumeUniversal`, ver
[[unified-logging-implementation-2026-07-28]]) fez 100% das linhas Lib/Decrypt
lançarem exceção, cada uma logada como `Warning` com stack trace completo —
dezenas de milhares de warnings em segundos. Combinado com
`rollOnFileSizeLimit`/`retainedFileCountLimit: 10` do Serilog, isso evictou
arquivos de log históricos **reais** desta máquina de dev (`layoutparserapi.log`
original e seu `_001`), porque o `Logging:File:Directory` default
(`C:\inetpub\wwwroot\layoutparser\api\logs`) já tinha dados reais acumulados —
não era um diretório "limpo" de dev. Perda não recuperável (log não versionado).
Detalhe completo: `.claude/agent-memory/lp-qa/unified-logging-parse-bug-and-log-dir-incident.md`.

**How to apply:** pra validar qualquer mudança em código que lê/escreve no
diretório de log configurado (`UnifiedLogReaderService` e afins), preferir
nesta ordem:
1. **Harness isolado sem subir a API inteira:** projeto console temporário
   (fora do repo, ex. na pasta scratchpad da sessão) com `ProjectReference`
   pro `.csproj` da API + `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
   (dá acesso a `Microsoft.Extensions.Configuration`/`Logging` sem precisar
   resolver versões de pacote manualmente). Instancia o serviço direto
   (`new UnifiedLogReaderService(configuration, logger)`) com um
   `IConfiguration` in-memory apontando `Logging:File:Directory` pra um
   `Path.GetTempPath()` descartável, e `ILoggerFactory.Create(b => b.AddConsole())`
   — nunca o Serilog real de arquivo. Gera as linhas de teste sinteticamente
   (formato real: `{DateTime:O} [LVL] [Corr:xxx] mensagem` pra Lib/Decrypt,
   `[yyyy-MM-dd HH:mm:ss.fff] [LVL] [Corr:xxx] [Src:xxx] mensagem` pra API).
   Este padrão foi usado e validado no fix do commit `975a84b`.
2. Se precisar validar contra os arquivos reais grandes (10390+15585 linhas),
   **copiar** os arquivos reais pra um diretório temporário isolado antes —
   nunca apontar a configuração pro diretório de produção original.
3. Antes de subir a API real e bater em qualquer endpoint que leia
   `Logging:File:Directory`, checar se esse diretório já tem dados reais
   acumulados — se tiver, tratar como produção, não como sandbox.
