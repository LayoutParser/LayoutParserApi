---
name: rollingfilelogger-vendoring-resync
description: Drift do RollingFileLogger vendorizado (Decrypt) resolvido 2026-07-28 — canonico venceu, Program.cs traduz args de CLI pra env vars em vez de Configure().
metadata:
  type: project
---

**Fato:** `LayoutParserDecrypt/LayoutParserLib/RollingFileLogger.cs` (cópia vendorizada, mesmo
namespace `LayoutParserLib`) tinha divergido do canônico em `LayoutParserLib/RollingFileLogger.cs`:
ganhou um `Configure(logDir, correlationId)` + campos estáticos que o canônico nunca teve (o
canônico sempre leu `LAYOUTPARSER_LOG_DIR`/`LAYOUTPARSER_CORRELATION_ID` via env var a cada
`Log()`). `LayoutParserDecrypt/Program.cs` chamava esse `Configure()` explicitamente.

Resolvido em 2026-07-28 (commit `e5ccceb` no repo Decrypt): ressincronizei a cópia vendorizada
1:1 com o canônico (removi `Configure`/campos) e troquei o call site em `Program.cs` por
`Environment.SetEnvironmentVariable("LAYOUTPARSER_LOG_DIR"/"LAYOUTPARSER_CORRELATION_ID", ...)`
a partir dos MESMOS args de CLI (`logDir`/`correlationId`, contrato de CLI congelado — ver
`conventions.md` §1 do repo Decrypt). Também atualizei `library-contract.md` do repo
`LayoutParserLib` (drift marcado como RESOLVIDO) e a memória de `lpd-dev` no repo Decrypt —
mas **NÃO** commitei a edição em `LayoutParserLib` (só doc, ficou pendente de commit nesse
terceiro repo — instrução do usuário pedia exatamente "dois commits, um por repo/tarefa").

**Why:** rejeitei a alternativa óbvia (adicionar `Configure()` ao canônico como wrapper) porque
isso mudaria o repo `LayoutParserLib` — fonte da verdade, referenciado como DLL pela API — só
para acomodar uma conveniência que era, na origem, o próprio drift. O `/sync-vendored-lib.md`
já declara "canônico vence, salvo decisão explícita do usuário", então inverter essa direção
exigiria justificativa que não existia aqui. A opção escolhida também tem zero risco pro
consumo real da lib pela API (`DecryptionService.cs` já seta as env vars via
`ProcessStartInfo.Environment` — comportamento inalterado nesse caminho) e conserta de brinde
o caminho de invocação manual do `.exe` (antes dependia só do `Configure()`, agora funciona
também via env var).

**How to apply:** se o comando `/sync-vendored-lib` (repo Decrypt) apontar divergência de novo
no futuro, o padrão é: canônico vence, e qualquer método/campo que só existe na cópia
vendorizada é suspeito de ser drift acidental — não assuma que "adicionar ao canônico" é a
correção default. Verifique sempre quem chama o método divergente (aqui, achei o call site
com `Grep` no `Program.cs`) antes de decidir qual lado ajustar. Validação sem fabricar payload
cripto real: rodar o `.exe` com input inválido de propósito ainda exercita o logger (a exceção
de cripto é capturada e logada antes do processo sair), então dá pra confirmar que o `logDir`/
`correlationId` custom chegaram corretos no `layoutparserlib.log` sem precisar de uma amostra
Sysmiddle real.
