---
name: unified-logging-implementation-2026-07-28
description: Implementação do logging unificado (POST api/logs/client + GET api/logs) — formato de linha real difere entre API e Lib/Decrypt, e testes com arquivo real de Lib/Decrypt ficaram pendentes.
metadata:
  type: project
---

Implementado em 2026-07-28 (commit `e8079d8`), desenho já fechado pela
arquiteta em [[unified-logging-and-multi-transform]]. Ver também
`.claude/agent-memory/lp-backend-dev/dead-logging-subsystem-removal-2026-07-27.md`
(o que ficou como logging efetivo antes desta mudança).

**Descoberta que corrigiu a premissa do pedido:** o dispatch assumia um único
formato de linha (`{timestamp:O} [{level}] [Corr:{corr}] {mensagem}`) pros 3
arquivos. Na prática são DOIS formatos diferentes:
- `layoutparserapi.log` (Serilog, `Program.cs`): `[yyyy-MM-dd HH:mm:ss.fff]
  [LVL] [Corr:xxx] [Src:xxx] mensagem` (colchetes, timestamp não-ISO,
  exceção em `{NewLine}{Exception}` — linhas seguintes, não ` | `).
- `layoutparserlib.log`/`layoutparserdecrypt.log` (`RollingFileLogger` nos
  repos Lib/Decrypt, fora deste escopo): `{DateTime:O} [LVL] [Corr:xxx]
  mensagem` (sem colchete no timestamp, ` | {ex}` concatenado na mesma
  linha lógica).

`UnifiedLogReaderService` usa dois regex (`ApiLinePattern`/
`SimpleLinePattern`) em vez de um único, com o grupo `[Src:...]` do
formato API opcional (linhas gravadas ANTES desta mudança não têm esse
campo — teriam quebrado o parse se fosse obrigatório).

**Why:** o formato do prompt original era uma simplificação; ler o código
real do `RollingFileLogger.cs` (via `.claude/tmp/servidor/.../LayoutParserLib`
e `.../LayoutParserDecrypt`, cópia read-only do servidor) antes de escrever
o parser evitou um parser que só funcionaria pra 1 dos 3 arquivos.

**How to apply:** em qualquer ajuste futuro do parser de log unificado,
confirmar o formato real lendo o `RollingFileLogger.cs` correspondente
(nesta máquina só existe via a cópia em `.claude/tmp/servidor/`, os repos
Lib/Decrypt não estão clonados aqui) em vez de assumir que os 3 arquivos
comungam um único formato.

**Pendência de teste:** nesta máquina de dev não existem
`layoutparserlib.log`/`layoutparserdecrypt.log` reais (só o
`layoutparserapi.log`, se a API já tiver rodado localmente) — o
`GET api/logs` não foi validado contra um arquivo real dessas duas fontes,
só contra o regex derivado do código-fonte. Validar quando houver acesso a
um ambiente com os 3 arquivos populados (ex. `BRNDDAPPBLD01` ou produção).

**Atualização 2026-07-28 (fix pós-incidente, commit `975a84b`):** o QA (Quinn)
achou em produção que o parse do timestamp Lib/Decrypt (`DateTimeStyles.RoundtripKind
| DateTimeStyles.AssumeUniversal`) **lança `ArgumentException`** — combinação
inválida no .NET, 100% das linhas Lib/Decrypt falhavam. O efeito colateral
(warning-com-stack-trace por linha) causou perda real de logs históricos via
rotação do Serilog. Fix: só `RoundtripKind`, e falha de parse por linha agora
agrega num único Warning por arquivo (não mais um por linha). Detalhe completo
da causa raiz e do incidente em
`.claude/agent-memory/lp-qa/unified-logging-parse-bug-and-log-dir-incident.md`
e a lição de processo em [[isolated-log-testing-harness-pattern]]. A pendência
de teste contra os 3 arquivos reais grandes (10390+15585 linhas) **continua
válida** — só foi validado com um harness isolado e linhas sintéticas, de
propósito, pra não repetir o incidente.

**Decisão de posicionamento:** endpoint de leitura foi pro `LogsController`
novo (`api/logs` GET + `api/logs/client` POST), não pro `MonitoringController`
existente — Monitoring é sobre análise/validação de layouts de negócio, um
domínio diferente de "operar arquivo de log". Ingestão+leitura como um único
recurso "logs" evita poluir o Monitoring.
