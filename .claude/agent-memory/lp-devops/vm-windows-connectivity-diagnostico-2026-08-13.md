---
name: vm-windows-connectivity-diagnostico-2026-08-13
description: Causa raiz real e fix da falha de conectividade Windows Server -> VM Ubuntu (Ollama), resolvida em 2026-08-13
metadata:
  type: project
---

Resolvido em 2026-08-13 (sessão com SSH direto na VM + PowerShell no Windows Server de produção,
fora do repo). Documento completo:
[`docs/architecture/diagnostico-conectividade-windows-vm-ubuntu.md`](../../../docs/architecture/diagnostico-conectividade-windows-vm-ubuntu.md)
(seção "RESOLVIDO (2026-08-13)").

**Causa raiz:** não era DHCP/ufw/firewall (as hipóteses do checklist anterior) — o NIC bridged da
VM `UBU220405RUN` no VirtualBox estava colado no **"Hyper-V Virtual Ethernet Adapter" (virtual)**
em vez do **"Realtek RTL8139/810x Family Fast Ethernet NIC" (físico, dono do IP `172.25.32.42`)**.
Por isso nunca havia rota ARP real do Windows Server até a VM, apesar da VM responder por outras
rotas (VPN) — isso mascarou o diagnóstico em sessões anteriores.

**Fixes aplicados:**
1. `VBoxManage controlvm "UBU220405RUN" nic1 bridged "Realtek RTL8139/810x Family Fast Ethernet NIC"`
2. `ollama.service` sem `OLLAMA_HOST` → só escutava `127.0.0.1`. Adicionado
   `Environment="OLLAMA_HOST=0.0.0.0"` no unit file + `daemon-reload` + `restart`.

**IP atual confirmado: `172.25.32.5`** — 4ª mudança por DHCP sem reserva fixa
(`.30`→`.31`→`.3`→`.5`, ver histórico em [[metrics-job-topology-vm]]). Recomendação de IP fixo
(reserva DHCP por MAC ou estático via netplan) continua **não implementada** — sem isso, a próxima
mudança de IP é questão de tempo, mesmo com o bug de bridge já corrigido.

**Config da API (`Ollama:Url`, não `Ollama:BaseUrl` — chave real confirmada em
`Services/XmlAnalysis/OllamaOptions.cs`):** o `appsettings.json` versionado no repo tem
`http://localhost:11434` — isso é o **default correto para dev** (Ollama local), não deveria
carregar o IP interno da VM (evita expor topologia interna no repo, alinhado com o motivo de
2026-08-12 que tornou os repos privados). Produção deve sobrescrever via env var `Ollama__Url` no
Environment do serviço Windows (mesmo mecanismo do `Database__Password`, ver `deploy.yml` linhas
~470-513) ou via `appsettings.Production.json` não versionado no disco do `.42`.

**Não verificado nesta sessão** (sem acesso remoto ao `.42` neste turno, só docs/config no repo):
se o `Ollama__Url`/`appsettings.Production.json` de produção já aponta para `172.25.32.5` ou ainda
para um IP antigo (`.3`/`.31`/`.30`). Quem tiver acesso RDP/PowerShell remoto ao `.42` deve rodar:
```powershell
reg query "HKLM\SYSTEM\CurrentControlSet\Services\LayoutParserApi\Environment" | findstr /i Ollama
Get-Content "C:\inetpub\wwwroot\layoutparser\api\appsettings.Production.json" -ErrorAction SilentlyContinue | Select-String Ollama
```
e corrigir para `http://172.25.32.5:11434` se estiver desatualizado.

Ver também [[prod-42-acesso-bloqueado]] (histórico de falta de acesso remoto) e
[[metrics-job-topology-vm]] (papel duplo da VM: Ollama + cron de métricas).
