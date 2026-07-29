---
name: unified-logging-parse-bug-and-log-dir-incident
description: QA gate 2026-07-28 do commit e8079d8 (logging unificado) achou bug crítico no parse de timestamp Lib/Decrypt + incidente de perda de log real de produção causado pelo próprio teste. RE-VALIDADO E FECHADO (PASS) com o fix 975a84b, testado com os arquivos reais desta máquina via harness isolado (sem subir a API).
metadata:
  type: project
---

**RE-GATE 2026-07-28 (PASS) — fix `975a84b` fecha a pendência:** revalidei contra os DOIS
arquivos reais desta máquina (`layoutparserlib.log`=10390 linhas, `layoutparserdecrypt.log`=15585
linhas), copiados para um diretório isolado no scratchpad (nunca apontei a API pro diretório de
produção). Harness: console app com `ProjectReference` pro `.csproj` da API, instanciando
`UnifiedLogReaderService` diretamente com `IConfiguration` in-memory (`Logging:File:Directory` =
diretório isolado) e um `ILogger<UnifiedLogReaderService>` fake que só CONTA por nível (nunca grava
em arquivo — zero risco de repetir o incidente). Resultado: `TotalCount` bate exatamente com a
contagem de linhas dos dois arquivos (10390 + 15585 = 25975), timestamps com `Kind=Utc` confirmado
por amostragem, **zero** linha malformada (Debug count = 0), **zero** warning-por-linha (só 3
warnings esperados de "arquivo não encontrado" pro `layoutparserapi.log`, que não copiei de
propósito), zero exception não tratada, ~260ms por chamada completa (26k linhas) — não proibitivo
pra paginação de UI. Confirmei também que os arquivos originais de produção não mudaram de conteúdo
por causa do teste (só cresceram naturalmente por uso real contínuo da máquina, mtime mudou entre a
listagem inicial e o fim do teste — nada a ver com o harness, que é read-only e nunca tocou o
diretório original). **Considero este escopo fechado — sem CONCERNS pendentes.**

**Bug crítico confirmado (FAIL):** `UnifiedLogReaderService.TryParseLine` (linha ~276) usa
`DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out timestamp)`
para o formato Lib/Decrypt (`SimpleLinePattern`). Essa combinação de `DateTimeStyles` é
**inválida no .NET** (`RoundtripKind` não pode ser combinado com `AssumeUniversal`/`AssumeLocal`/
`AdjustToUniversal`) e lança `ArgumentException` — **sempre**, para TODA linha Lib/Decrypt real,
sem exceção. O efeito prático: `GET api/logs` nunca retorna nenhuma entrada `Source=Lib` ou
`Source=Decrypt` (confirmado com os arquivos reais de produção nesta máquina: 10390 +15585
linhas, 100% falharam). Fix sugerido e validado num harness isolado: usar `DateTimeStyles.RoundtripKind`
sozinho (sem `AssumeUniversal`) — parseia corretamente e preserva `Kind=Utc` a partir do sufixo `Z`
do formato `:O`. Repassado ao Dex como CONCERNS acionável — não corrigi eu mesma (fora do escopo QA).

**Efeito colateral grave descoberto durante o teste:** cada linha que falha é capturada pelo
try/catch **por linha** em `ParseLines`, mas gera um `_logger.LogWarning(ex, ...)` com stack trace
completo — ou seja, cada chamada real a `GET api/logs` (que lê os 3 arquivos, incluindo Lib+Decrypt)
gera **dezenas de milhares** de linhas WARN com stack trace na saída do próprio Serilog do backend.
Isso é auto-amplificador: o arquivo `layoutparserapi.log` cresce tão rápido que estoura o
`fileSizeLimitBytes` (~2MB) e rola dezenas de vezes em segundos.

**Incidente real causado por este teste:** o `Logging:File:Directory` configurado em
`appsettings.json` (`C:\inetpub\wwwroot\layoutparser\api\logs`) **já existe nesta máquina de dev**
com dados reais acumulados dos 3 arquivos (não é um dev machine "limpo" — compartilha o mesmo
caminho que produção usaria). Ao subir a API real (`dotnet run`) e chamar `GET api/logs` só
DUAS vezes via curl para validar o endpoint, o bug acima dparou uma tempestade de warnings que,
combinada com `retainedFileCountLimit: 10` do Serilog, **evictou/sobrescreveu arquivos de log
históricos reais** (`layoutparserapi.log` original de 21/07 e `_001` de 28/07 02:00 foram perdidos,
substituídos por `_016`–`_025` cheios de spam). Processo morto assim que percebido (`taskkill /F`).
Sem como recuperar o conteúdo evictado (não versionado, fora do git).

**Why:** o formato real diverge do assumido pelo dev original (que não tinha os arquivos Lib/Decrypt
reais nesta máquina no momento da implementação — ver memória do lp-parser-llm
`rollingfilelogger-vendoring-resync` e do lp-backend-dev `unified-logging-implementation-2026-07-28`,
que registram a mesma pendência de validação). Validar com arquivo real (não só regex de olho) é
exatamente o que expôs o bug — reforça por que este QA gate existe.

**How to apply:** (1) qualquer revisão futura do parser de log deve testar com os 3 arquivos reais
desta máquina antes de aprovar — eles existem em `C:\inetpub\wwwroot\layoutparser\api\logs` e são
grandes (dezenas de milhares de linhas cada). (2) **NUNCA** subir a API real (`dotnet run`) e bater
em `GET api/logs` contra esse diretório default enquanto o bug de parse não estiver corrigido —
prefira sempre um `Logging:File:Directory` isolado (env var/config override) apontando pra uma cópia
dos arquivos, ou instanciar `UnifiedLogReaderService` diretamente (via harness/projeto de teste com
`ProjectReference`) usando só `ILoggerFactory.Create(b => b.AddConsole())` (nunca o Serilog real de
arquivo) para leitura read-only seguro. (3) Antes de rodar qualquer teste que grave no
`Logging:File:Directory` configurado, checar se esse diretório já tem dados reais — se tiver, tratar
como produção, não como sandbox.
