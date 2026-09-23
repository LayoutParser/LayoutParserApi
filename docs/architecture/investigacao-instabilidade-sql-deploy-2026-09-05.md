# Investigação — instabilidade do smoke test de deploy (/health/ready 503/timeout) — 2026-09-05

Disparado por: falhas recorrentes do step "Iniciar servico e smoke test" em `ci-dev.yml`
(develop) e efeito colateral no gate de `deploy.yml` (master) através do PR de sync
`develop→master`. Issue relacionada de fundo: #110
(`docs/architecture/dry-run-config-drift-producao-2026-09-03.md`).

## Resumo executivo

**Causa mais provável: instabilidade transitória de conectividade/latência com o SQL Server em
`172.31.249.51`, agravada por um timeout do smoke test de CI (5s) menor que o pior caso do
próprio health check de SQL (até ~6s) — não uma credencial revogada/errada.** Não encontrei
evidência de "Login failed" nos runs de CI analisados (o corpo da resposta nunca chegou a ser
capturado pelo script, ver achado 4). Não tenho acesso ao host de produção nem ao SQL Server
para confirmar 100% — ver limitações no fim.

## O que foi observado

### 1. Janela temporal — não é um estado permanente desde um commit específico

Runs de `ci-dev.yml` (workflow que dá o gate de PR contra `develop`, e cujo resultado também
aparece como check `build` no PR `#310` de sync `develop→master`):

| Run | Branch/PR | Horário (UTC) | Resultado |
|---|---|---|---|
| 33959972955 (PR #305) | develop | 10:11 | ✅ sucesso |
| 33881277126 | feat/auth-m2m-... | 10:01 | ✅ sucesso |
| 33961428213 | feat/honeypot-canary... | 10:43 | cancelled (2m32s) |
| **33961538463** | feat/honeypot-canary... | **10:46** | **✅ sucesso (21m53s — passou só depois do restart)** |
| 33961742327 | feat/fiscal-projects-revisions... | 10:50 | ❌ falha (41m3s) |
| 33961308972 | feat/mapping-releases-listagem | 10:52 | ❌ falha (56m29s) |
| 33963626282 | feat/honeypot-canary... (2ª tentativa) | 11:33 | ❌ falha (30m47s) |
| 33968710427 | PR #307→develop (= check do PR #310) | 13:22 | ❌ falha (12m23s) |

Ou seja: a instabilidade **começou por volta de 10:43-10:50 de hoje** (não estava presente nos
runs de #305/#301 mais cedo), mas **não é 100% determinística** — o run 33961538463
(honeypot-canary, 10:46) passou, ainda que só depois do restart automático de segurança. Runs de
branches completamente diferentes (auth M2M, honeypot/canary, fiscal-projects, mapping-releases)
falharam do mesmo jeito, o que **descarta uma regressão de código isolada em algum desses PRs**
como causa raiz — o padrão é transversal a branches sem relação entre si, apontando para algo
externo ao código (SQL/rede), não um bug introduzido por uma dessas features.

`deploy.yml` (produção/master): o último sucesso registrado foi o run `33960280800` (PR #306,
10:18:06Z) — **antes** da janela de instabilidade acima. Não há ainda um run de `deploy.yml` que
tenha efetivamente falhado por este motivo (o PR #310, que traria o próximo push a `master`,
está bloqueado pelo check `build` do `ci-dev.yml`, então o deploy de produção real nem chegou a
rodar ainda para esse conteúdo — o bloqueio está a montante, em `develop`).

### 2. Padrão do smoke test: maioria "sem resposta" (timeout), minoria HTTP 503

Nos 4 runs falhos inspecionados em detalhe (log completo via `gh run view --log`), o padrão é:
dezenas de tentativas (24 tentativas × 2 janelas, antes/depois do restart automático) onde a
resposta é predominantemente **"sem resposta"** (o cliente PowerShell não recebeu HTTP algum
dentro de 5s) intercalada com alguns **"HTTP 503"** — nunca uma sequência 100% de 503 nem 100%
timeout. Um dos runs (33961742327, 11:22-11:31) teve as **48 tentativas** (2 janelas completas)
todas "sem resposta", sem nenhum 503 sequer.

Essa mistura errática — às vezes 503 rápido, às vezes nada por 5s, às vezes um restart resolve
(33961538463) e às vezes nem o restart resolve — é o padrão típico de **latência
inconsistente/dependência externa instável**, não de uma falha determinística (config quebrada,
firewall bloqueando 100% do tráfego, ou credencial sempre inválida produziriam o mesmo resultado
em toda tentativa, não esse mix).

### 3. `Services/Health/ReadinessHealthChecks.cs` — `SqlServerHealthCheck`

```csharp
"TrustServerCertificate=true;Encrypt={encrypt};" +
"Connection Timeout=3;Command Timeout=3;Pooling=true;"
```

- Timeout é **deliberadamente curto** (comentário no código: "é sonda, não caminho de dados").
  Isso é correto como princípio, mas tem uma consequência não documentada: no pior caso, a sonda
  pode levar até ~6s (3s de handshake de conexão + até 3s de `SELECT 1`) antes de retornar
  Unhealthy — **mais que os 5s de `-TimeoutSec` do `Invoke-WebRequest` do smoke test**
  (`ci-dev.yml`/`deploy.yml`). Quando o SQL está mesmo que levemente lento (não fora do ar, só
  lento), o resultado do lado do smoke test é indistinguível de "servidor não respondeu nada" —
  exatamente o "sem resposta" predominante nos logs. Isso **não é a causa raiz da instabilidade
  do SQL em si**, mas é um amplificador: transforma qualquer lentidão real do SQL (que sozinha
  seria só um 503 com corpo explicativo) em timeout sem corpo nenhum, dificultando o diagnóstico
  a partir do próprio smoke test.
- É ajustável via config? Não — os timeouts (`Connection Timeout=3`, `Command Timeout=3`) estão
  **hardcoded** na string de conexão dentro do health check, não lidos de `Database:*`
  (`ConnectionTimeout`/`CommandTimeout` em `appsettings.json` são usados só pelo caminho de dados
  real, ex. `LayoutDatabaseService`, não por esta sonda). Ajustar o timeout da sonda exige mudança
  de código (`@lp-backend-dev`), não de config.

### 4. Achado colateral no próprio script de smoke test: corpo do 503 nunca aparece no log

O script (`ci-dev.yml`/`deploy.yml`, função `Test-Readiness`) tenta capturar o corpo da resposta
mesmo em erro:

```powershell
if ($r) {
  try {
    $stream = $r.GetResponseStream()
    $reader = New-Object System.IO.StreamReader($stream)
    $corpo = $reader.ReadToEnd()
  } catch { }
}
...
if ($corpo) { Write-Host "    corpo: $corpo" }
```

Busquei `corpo:` em todos os logs completos dos runs falhos analisados hoje (`gh run view --log`)
e **nunca apareceu** — nem nos casos de HTTP 503 (que deveriam ter corpo JSON com o detalhe de
qual dependência está Unhealthy, conforme o payload rico que `CatalogHealthCheck`/
`SqlServerHealthCheck` produzem). Isso significa que, mesmo quando o servidor respondeu 503, o
`catch { }` silencioso engoliu alguma exceção ao ler o `ResponseStream` (comportamento comum
quando `Invoke-WebRequest` já consumiu/descartou o stream antes do bloco `catch`, ou quando o
corpo já foi lido internamente e `GetResponseStream()` retorna vazio/fechado — bug conhecido de
`WebException` com `Invoke-WebRequest` em certas versões do PowerShell). **Efeito prático: não
dá pra confirmar OU descartar "Login failed" a partir dos logs de CI disponíveis, porque o
mecanismo de diagnóstico do próprio smoke test está quebrado silenciosamente.** Isso é
independente do achado #3 (timeout curto) e vale a pena corrigir também, por ser puramente
observabilidade (sem risco de mascarar nada).

### 5. Conectividade de rede ao SQL Server — parcialmente verificada

A partir desta máquina (workstation `NDD-NOT-10910`, não o runner de CI nem o host de produção),
testei TCP puro à porta do SQL Server:

```
python3 socket.connect(('172.31.249.51', 1433)) → PORT OPEN
```

A porta está aberta e aceitando conexões TCP a partir desta rede. Isso **não prova** que o
caminho de rede do runner de CI (self-hosted, outra máquina) ou do host de produção (`.42`) até
o mesmo SQL Server esteja igualmente saudável — só descarta "o SQL Server está completamente
fora do ar/porta fechada para toda a rede", que era a hipótese (a) mais extrema. Não tive acesso
SSH/RDP ao host de produção nem ao servidor de banco (confirma achado já registrado em memória:
`172.25.32.42` — host de produção — está bloqueado para este agente; o SQL fica em `172.31.249.51`,
nem tentado por essa via).

### 6. Nenhuma mudança recente de config/rede/firewall que explique o início da instabilidade

`git log` (branches locais/remotas, desde 2026-09-01) não mostra nenhum commit tocando firewall,
`Database:*` em `appsettings.json`, string de conexão SQL, ou isolamento de rede além do que já
estava documentado (Kestrel loopback, `IdentityDatabase` local dedicado — ambos anteriores e sem
relação com `Database:Server=172.31.249.51`). Os commits do período da janela de instabilidade
(honeypot/canary M2M, mapping-release listagem, TTL de sessão IA) não tocam `Services/Database`
nem a seção `Database` de config. Isso reforça que a causa não está em código deste repositório.

## Diferenciação pedida: timeout vs "Login failed" (credencial)

**Não encontrei, nos runs de CI de hoje, nenhuma evidência textual de "Login failed for user
macgyver"** — só porque o mecanismo de captura do corpo da resposta está quebrado (achado #4),
não porque descartei a hipótese. O padrão observado (mistura de timeout/503, intermitente,
transversal a branches não relacionadas, às vezes resolvido por restart) é **mais consistente
com lentidão/instabilidade transitória de rede ou carga no SQL do que com uma credencial sempre
inválida** — uma senha errada/expirada tende a falhar de forma **determinística e imediata**
(login rejeitado em milissegundos, não em ~3-6s de timeout), o que não bate com o padrão de
"sem resposta" predominante aqui. Ainda assim, **não posso descartar 100%** que uma fração das
falhas seja por credencial — sem o corpo da resposta capturado, é uma lacuna real de evidência.

## Recomendações (não implementadas — aguardando aprovação)

1. **[Observabilidade, baixo risco] Corrigir a captura do corpo da resposta no smoke test**
   (`ci-dev.yml` e `deploy.yml`, função `Test-Readiness`) — trocar o padrão
   `Invoke-WebRequest` + `try/catch` por `Invoke-WebRequest -SkipHttpErrorCheck` (PowerShell 7,
   já é o shell usado, confirmado no log: `pwsh.EXE`) ou por `HttpClient` puro, que preserva o
   corpo em respostas de erro sem depender de `WebException.Response.GetResponseStream()`. Sem
   isso, todo próximo incidente similar vai repetir a mesma lacuna de diagnóstico. Dono:
   `@lp-devops`, com o próprio dono aprovando antes de tocar no workflow.
2. **[Observabilidade, baixo risco] Aumentar o `-TimeoutSec` do smoke test de 5s para algo como
   10s**, para não confundir "SQL um pouco lento" com "sem resposta". Isso não mascara uma falha
   real (se o SQL estiver de fato fora, 10s de timeout ainda vai estourar e reportar falha) — só
   evita que uma sonda de SQL com pior caso de ~6s seja cortada pela metade do smoke test. Risco
   de mascarar: nenhum identificado, mas fica para o dono confirmar antes de aplicar, por tocar
   `deploy.yml` (produção).
3. **[Investigação adicional, fora do alcance deste agente] Confirmar com o time de infra/DBA se
   houve throttling, sobrecarga ou manutenção no SQL Server `172.31.249.51`/banco
   `ConnectUS_Macgyver` na janela de hoje entre ~10:43 e ~13:33 UTC.** Dado que a credencial é
   compartilhada por ~231.890 times na NDD (`.claude/rules/security.md`), um pico de uso de
   qualquer um dos outros consumidores dessa credencial/servidor pode gerar exatamente esse tipo
   de lentidão intermitente sem que nada tenha mudado neste repositório. Esse é o caminho mais
   provável para uma causa raiz definitiva, mas exige acesso que não tenho (rede/monitoramento do
   SQL Server em si).
4. **Não recomendo reabrir a hipótese de credencial revogada/rotacionada por outro time** com base
   apenas nesta investigação — o padrão observado não bate com login rejeitado de forma
   consistente. Se o corpo da resposta for corrigido (recomendação 1) e um próximo run mostrar
   "Login failed" explicitamente, essa hipótese volta à mesa com evidência concreta.

## Limitações desta investigação

- Sem acesso SSH/RDP/WinRM ao host de produção (`172.25.32.42`) nem ao runner self-hosted de CI
  dev — não consegui inspecionar logs do processo `LayoutParserApi.exe` (Serilog) diretamente, só
  o que o smoke test do GitHub Actions expõe.
- Sem acesso ao SQL Server (`172.31.249.51`) além do teste de porta TCP a partir desta
  workstation — não consegui autenticar como `macgyver` para confirmar/descartar "Login failed"
  de forma independente do smoke test.
- O teste de porta TCP feito nesta sessão parte de uma rede diferente da do runner/produção —
  corrobora que o servidor está no ar, mas não é prova definitiva do caminho de rede exato que
  falhou nos runs de CI.
