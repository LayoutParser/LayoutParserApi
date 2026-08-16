# Diagnóstico — deploy de produção abortado, readiness "sem resposta" (2026-08-15)

## Sintoma
24/24 tentativas de `GET /health/ready` sem NENHUMA resposta HTTP (não é 503/Degraded).
Isso indica processo que não abriu a porta — crash no boot, não dependência degradada.

## O que já foi descartado / confirmado (evidência de código, sem acesso ao host)

1. **Não é a PR #115 — não existe.** O número correto é **PR #114**
   (`fix/config-validation-lowcode-ollama`), que introduz o `ValidateOnStart()` em
   `LowCodeRunnerOptions`. Confirmado via `git log`: `81a23bc` → merge `0a7cb7f` (#114) →
   merge `f717bb3` (#116, `develop`→`master`). **Ambos já estão em `master`.**
2. **A correlação com #114 é plausível, não hipotética — é a hipótese líder.**
   `Program.cs:469-492`: `AddOptions<LowCodeRunnerOptions>().Bind(...).Validate(...).ValidateOnStart()`.
   A regra: se a seção `LowCode` **existe** na config do host **e** `AllowedPackageGuids` está
   vazio, o boot **falha com exceção** (`ValidateOnStart` lança no primeiro acesso a `IOptions`,
   durante o startup do host — antes do Kestrel abrir a porta). Se a seção estiver **ausente**,
   o boot passa normalmente (`lowCodeSection.Exists() == false` → `return true`).
   Isso bate exatamente com "sem resposta" (processo morre antes de bindar a porta), diferente
   de `Degraded` (que exigiria o processo de pé).
3. **PR #109 (push direto anterior)** foi só documentação (`docs/architecture/...`), sem tocar
   `Program.cs`/boot — não é candidata a esta falha.
4. **`Kestrel:Endpoints:Http:Url = http://0.0.0.0:5000`** é padrão correto e não explica
   ausência total de resposta local (`0.0.0.0` escuta em `localhost`); não há motivo, só por
   isso, para "sem resposta" — mantido como hipótese de baixa prioridade (firewall/binding).
5. **`LowCode`/`Ollama:Url` órfãos (achado anterior)** causam **`Degraded`** via health check,
   não silêncio total — não são, por si só, a causa deste sintoma. Mas são o motivo pelo qual a
   seção `LowCode` pode estar **parcialmente presente** no host (alguém já mexeu nela) — o que é
   justamente a pré-condição para o `ValidateOnStart` disparar.

## Hipóteses priorizadas

### 1. (mais provável) `ValidateOnStart` de #114 derruba o boot em produção
A seção `LowCode` existe no `appsettings`/env do host de produção, mas `AllowedPackageGuids`
está vazio (ou não foi propagado como `LowCode__AllowedPackageGuids__0` na env do serviço
Windows). Antes de #114 isso silenciosamente virava `IN (NULL)` (bug já mapeado); agora o boot
**morre** com exceção não tratada, e como não há resposta HTTP, o smoke test nunca vê nem 503.

**Confirmar no host:**
```powershell
Get-Service LayoutParserApi
Get-EventLog -LogName Application -Source "LayoutParserApi" -Newest 20
# ou, se rodar via SCM puro sem EventLog customizado:
Get-WinEvent -LogName Application -MaxEvents 50 | Where-Object {$_.Message -match "LayoutParserApi|LowCode"}
# arquivo de log Serilog (Logging:File:Directory do appsettings do host, tipicamente):
Get-Content "C:\inetpub\wwwroot\layoutparser\Logs\layoutparserapi*.log" -Tail 100
# variavel de ambiente do servico:
reg query "HKLM\SYSTEM\CurrentControlSet\Services\LayoutParserApi" /v Environment
```
Procurar por: `LowCode:AllowedPackageGuids esta vazio` (mensagem exata do `.Validate()` em
`Program.cs:487-491`) ou qualquer exceção logo após `[BOOTSTRAP]`.

### 2. Outro `throw`/exceção não tratada no boot (fora do que #114 adicionou)
Releitura de `Program.cs` em `master` não achou outro ponto óbvio de `throw` além do try/catch
global (linha 785, que loga e propaga). Se a hipótese 1 for descartada, procurar no log de boot
qualquer stack trace antes do "Application started".

### 3. Firewall/binding específico do Windows Server 2022 (baixa prioridade)
Confirmar se a porta 5000 está de fato ouvindo no host:
```powershell
Get-NetTCPConnection -LocalPort 5000 -State Listen
netstat -ano | findstr :5000
```
Se não houver nada ouvindo, reforça hipótese 1/2 (processo não chegou a bindar). Se houver algo
ouvindo mas o smoke test (rodando de outra máquina/runner) não alcança, aí sim é rede/firewall.

## Comando único para o dono rodar primeiro

```powershell
Get-Content "C:\inetpub\wwwroot\layoutparser\Logs\layoutparserapi*.log" -Tail 100
```
Se aparecer a mensagem `LowCode:AllowedPackageGuids esta vazio, mas a secao LowCode esta
presente...`, hipótese 1 confirmada — corrigir a env do serviço (`LowCode__AllowedPackageGuids__0=...`
ou remover a seção `LowCode` inteira do host) e reiniciar.
