# Diagnóstico — conectividade Windows Server (produção) ↔ VM Ubuntu (Ollama)

## RESOLVIDO (2026-08-13)

Diagnóstico confirmado numa sessão posterior, com acesso via SSH direto na VM + PowerShell no
Windows Server de produção (fora do repo).

**Causa raiz real:** nenhuma das hipóteses do checklist original (DHCP, `ufw`, Ollama caído,
firewall do Windows) era o problema principal. O NIC do VirtualBox da VM (`UBU220405RUN`) estava
sim configurado em modo **Bridged** — mas colado no adaptador **errado**: o
**"Hyper-V Virtual Ethernet Adapter"** (virtual, sem rota real até a rede `172.25.32.0/22`) em vez
do **"Realtek RTL8139/810x Family Fast Ethernet NIC"** (o adaptador físico que carrega o IP
`172.25.32.42` do host). Por isso o Windows Server nunca via a VM por ARP na rede real — mesmo a
VM sendo alcançável por outras rotas (ex.: VPN), o que mascarou o sintoma e alimentou as hipóteses
erradas (achado "bridged vs NAT" de 2026-07-31 estava na direção certa, mas não fechou o
diagnóstico até esta sessão).

**Fixes aplicados ao vivo:**

1. **Bridge no adaptador certo:**
   ```
   VBoxManage controlvm "UBU220405RUN" nic1 bridged "Realtek RTL8139/810x Family Fast Ethernet NIC"
   ```
   Confirmado do Windows Server: `Test-NetConnection -ComputerName 172.25.32.5 -Port 11434` →
   `TcpTestSucceeded: True`.
2. **`ollama.service` não tinha `OLLAMA_HOST` configurado** — escutava só em `127.0.0.1`, então
   mesmo com a rede corrigida a porta 11434 não estaria acessível de fora da VM. Corrigido
   adicionando `Environment="OLLAMA_HOST=0.0.0.0"` no unit file do systemd, seguido de
   `daemon-reload` + `restart`.

**IP atual confirmado da VM: `172.25.32.5`** — 4ª mudança documentada por DHCP sem reserva fixa
(`.30` → `.31` → `.3` → `.5`). A recomendação da seção 4 abaixo (IP fixo) permanece válida e fica
**reforçada**: mesmo com o bug de bridge corrigido, o IP vai continuar mudando a cada renovação de
lease/reboot até que uma reserva DHCP por MAC (ou IP estático via `netplan`) seja aplicada — sem
isso, o próximo incidente de conectividade é questão de tempo, ainda que por uma causa mais simples
(IP mudou) do que a raiz real desta vez (bridge errado).

**Lição para o checklist original:** a ordem de verificação sugerida na seção 2 (DHCP → serviço →
ufw → adaptador → VM desligada → firewall Windows) deveria ter colocado a checagem do adaptador
(item 4) **mais cedo** — o achado de 2026-07-31 já apontava nessa direção e ficou soterrado atrás
de hipóteses mais "óbvias". Para o próximo incidente de rede nesta VM, checar
`VBoxManage showvminfo "UBU220405RUN" | findstr /i "NIC"` **primeiro**, comparando o adaptador
bridged contra a lista de NICs físicas reais do host (`Get-NetAdapter` no Windows), não assumir que
"está em modo Bridged" já significa "está bridged no adaptador certo".

---

**Status (histórico, sessão anterior):** falha reportada pelo dono em 2026-08-13. Diagnóstico feito **sem acesso remoto**
nesta sessão (workstation não tem SSH/WinRM/SMB/RPC para `172.25.32.42`, ver
[`.claude/agent-memory/lp-devops/prod-42-acesso-bloqueado.md`](../../.claude/agent-memory/lp-devops/prod-42-acesso-bloqueado.md);
e a tentativa de SSH direto na VM via `layoutparser_automation` foi bloqueada pelo classificador
de auto mode nesta sessão). Este documento é **checklist acionável**, não diagnóstico confirmado.

## 1. O que a memória do projeto já documentava

- **Topologia:** VM Ubuntu roda em **VirtualBox**, no **mesmo hardware físico** do Windows Server
  2022 de produção (`172.25.32.42`, host `BRNDDAPPBLD01`) — sem isolamento real de CPU/RAM, só
  overhead de virtualização. VM = `UBU220405RUN`, hospeda **Ollama** (`OLLAMA_HOST=0.0.0.0`,
  `ufw` liberando porta 11434 só para o IP do Windows Server) **e** o cron semanal do job de
  métricas de IA (`metrics-batch`, sábado 00:00) — ver
  [`metrics-job-topology-vm.md`](../../.claude/agent-memory/lp-devops/metrics-job-topology-vm.md).
- **IP histórico instável — 3 mudanças por DHCP sem reserva fixa, já confirmadas:**
  `172.25.32.30` → `172.25.32.31` (2026-07-29) → `172.25.32.3` (2026-07-31). Ou seja, **o IP já
  mudou 3 vezes** e é provável que tenha mudado de novo — **não assumir `.31` como atual sem
  confirmar**. Fonte: memória `deploy-production-topology` (auto-memory do usuário,
  `originSessionId: cf5a9d96-...`, 2026-08-01).
- **Achado de rede não resolvido:** em 2026-07-31 havia indício de que a VM pode ter deixado de
  ser *bridged* e passado a **compartilhar o IP do host com port-forwarding** (a porta 22 do
  `.3` respondia como a VM, mas o usuário identificava esse IP como o próprio `WINSRV2022-LIB`) —
  **nunca verificado a fundo**, ficou como hipótese aberta.
- **Incidente anterior de indisponibilidade total:** em 2026-07-31 a VM ficou **totalmente
  inacessível** (sem ICMP, sem porta 22, sem porta 11434) na véspera da rodada de sábado, com o
  `tracert` morrendo logo depois do gateway (que respondia normalmente) — ou seja, "VPN/rota até
  o gateway OK, host da VM fora". Não foi root-caused; o desenho de resposta foi só defensivo
  (job trata origem ausente como WARN, não erro).
- **Precedente de rede enganoso (fora desta VM, mas mesmo host físico):** o incidente do runner
  self-hosted (`deploy-runner-mtu-blackhole`) parecia MTU/PMTUD mas na real era um dispositivo
  de rede stateful matando conexões longas ociosas — lição aplicável aqui: **não fixar a primeira
  hipótese (ex. "é o firewall da VM") sem eliminar as outras.**
- **VPN do usuário caiu 2x durante provisionamento da VM** (2026-07-30) — sintoma foi
  "destination host unreachable" a partir do gateway interno `10.254.254.2` (firewall
  `br-ndd-fw01`), não um erro óbvio de VPN. Vale descartar isso quando o diagnóstico partir da
  workstation em vez de dentro da rede NDD.
- **A VM não tem sudo para o usuário `elson`** — qualquer comando de diagnóstico que exija
  privilégio (`ufw`, `systemctl restart`, reconfigurar `netplan`) pode precisar de outro usuário
  ou acesso físico/console.

## 2. Causas prováveis (ordem sugerida de verificação — mais barato/provável primeiro)

1. **IP da VM mudou de novo por DHCP sem reserva fixa** (já aconteceu 3x). A API aponta
   `Ollama:BaseUrl` para um IP fixo em config — se o DHCP renovou o lease, a API está batendo
   num IP morto ou ocupado por outra máquina.
2. **Serviço Ollama caído dentro da VM** (`systemctl status ollama` não `active`) — pode ter
   caído sozinho, por reboot do host físico (VirtualBox sobe a VM automaticamente? verificar) ou
   por falta de memória (CPU-only, sem GPU, competindo com o Windows Server hospedeiro).
3. **`ufw` bloqueando o IP atual do Windows Server** — o `ufw` foi configurado para liberar 11434
   **só** para o IP do `.42` na época; se o IP do `.42` também mudasse (é físico, improvável mas
   verificar) ou se a regra do `ufw` nunca foi atualizada após alguma reinstalação, a porta fica
   fechada para quem precisa.
4. **Adaptador de rede da VM mal configurado no VirtualBox** — bridged vs NAT vs host-only.
   Achado não resolvido de 2026-07-31 sugere que pode ter mudado de bridged para
   NAT-com-port-forward em algum momento; se isso aconteceu de novo (ou parcialmente), a VM
   pode não estar mais na mesma sub-rede `172.25.32.0/22` acessível pelo host.
5. **VM desligada / não voltou depois de reboot do host físico** — já houve indisponibilidade
   total documentada (2026-07-31); VirtualBox não necessariamente sobe a VM automaticamente após
   reboot do Windows Server hospedeiro, a menos que esteja configurado como serviço/autostart.
6. **Firewall do Windows Server (produção) bloqueando saída** para a porta 11434 — menos
   provável (nada documentado nesse sentido), mas vale descartar já que é rápido de checar do
   lado Windows.

## 3. Checklist de comandos — para quem tiver acesso físico/RDP ao `.42`

### 3.1 Lado Windows Server (dentro do `.42`, via RDP interativo — não há canal remoto hoje)

```powershell
# 1. Confirmar se a VM está rodando no VirtualBox
& "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe" list runningvms

# 2. Descobrir o IP atual da VM (se houver acesso ao console dela via VirtualBox GUI/VBoxManage)
& "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe" guestproperty enumerate "UBU220405RUN" | findstr /i "IP"

# 3. Testar conectividade TCP na porta do Ollama (ajustar IP conforme achado acima)
Test-NetConnection -ComputerName 172.25.32.3 -Port 11434
Test-NetConnection -ComputerName 172.25.32.31 -Port 11434   # IP anterior, testar os dois

# 4. Testar SSH também (porta 22), útil pro job de métricas
Test-NetConnection -ComputerName <ip-vm-atual> -Port 22

# 5. Conferir se a API de produção realmente aponta para o IP certo
Get-Content "C:\inetpub\wwwroot\layoutparser\api\appsettings.json" | Select-String -Pattern "Ollama"
# ou, se veio de env var do serviço (ver rules/security.md):
reg query "HKLM\SYSTEM\CurrentControlSet\Services\LayoutParserApi\Environment"
```

### 3.2 Lado da VM Ubuntu (via console do VirtualBox, já que SSH pode estar indisponível — ou via SSH se a rede permitir)

```bash
# 1. IP atual e interface
ip addr show

# 2. Ollama está rodando?
systemctl status ollama
journalctl -u ollama -n 50 --no-pager   # se caído, ver por quê (OOM é candidato dado CPU-only)

# 3. Ollama está escutando em 0.0.0.0, não só 127.0.0.1?
ss -tlnp | grep 11434

# 4. Regras de firewall — liberando o IP correto do Windows Server?
sudo ufw status verbose
# comparar o IP liberado ali com o IP ATUAL do .42 (confirmar que não mudou)

# 5. Cron do job de métricas ainda agendado? (verificação lateral, já que a VM tem duplo papel)
crontab -l

# 6. Adaptador de rede — bridged ou NAT? (rodar de dentro da VM não mostra isso diretamente;
#    precisa checar a config da VM no VirtualBox — VBoxManage showvminfo do lado Windows)
```

```powershell
# Do lado Windows, checar o tipo de adaptador configurado pra VM:
& "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe" showvminfo "UBU220405RUN" | findstr /i "NIC"
```

### 3.3 Teste de ponta a ponta (depois de confirmar IP e serviço up)

```powershell
# Do Windows Server, simular a chamada que a API faria:
Invoke-WebRequest -Uri "http://<ip-vm-atual>:11434/api/tags" -UseBasicParsing
```

## 4. Recomendação — resolver a causa raiz recorrente

O IP da VM já mudou **3 vezes por DHCP sem reserva fixa** (`.30` → `.31` → `.3`), e cada mudança
já causou (ou pode ter causado) exatamente este tipo de falha de conectividade. Recomendação:

1. **Reservar IP fixo para a VM** no DHCP da rede NDD (por MAC address da NIC virtual) — elimina
   a causa raiz de forma definitiva, sem exigir reconfiguração manual após cada reboot/renovação
   de lease.
2. **Alternativa mais simples se não houver acesso ao DHCP da rede:** configurar IP estático
   direto na VM (`netplan`, já que é Ubuntu 24.04 com netplan) — mais frágil a longo prazo (não
   se auto-corrige se a topologia de rede mudar), mas resolve o sintoma imediato sem depender de
   outra equipe.
3. Depois de fixar o IP, **atualizar `Ollama:BaseUrl`** na configuração da API de produção (fora
   do `appsettings.json` versionado — via env var do serviço, seguindo o padrão já em uso) e
   **atualizar a regra do `ufw`** na VM para o IP correto do `.42` (que é físico e estável).
4. Documentar o IP fixo escolhido na memória do projeto
   (`.claude/agent-memory/lp-devops/metrics-job-topology-vm.md` ou nova entrada) para não repetir
   o ciclo "descobrir o IP de novo por SSH" a cada incidente.

## 5. Limitação desta sessão

Este documento foi produzido **sem verificação remota** — nem via SSH na VM (bloqueado pelo
classificador de auto mode desta sessão) nem via RDP/console no `.42` (sem acesso desde esta
workstation, ver `prod-42-acesso-bloqueado.md`). Antes de agir sobre qualquer item da seção 2,
alguém com acesso físico ou RDP ao `.42` precisa rodar os comandos da seção 3 e confirmar qual
causa realmente se aplica.
