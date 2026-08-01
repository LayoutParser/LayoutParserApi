---
name: prod-42-acesso-bloqueado
description: O servidor de produção 172.25.32.42 NÃO é administrável a partir desta workstation (SSH/WinRM/SMB/RPC todos negados) — só RDP interativo; o desbloqueio é anexar a chave pública da workstation no administrators_authorized_keys do .42
metadata:
  type: project
---

Verificado em 2026-07-31 (missão da ponte de log AiMetrics): **nenhum canal não-interativo**
de administração do `172.25.32.42` funciona a partir da workstation `NDD-NOT-10910`.

| Canal | Comando | Resultado |
|-------|---------|-----------|
| SSH | `ssh -i <id_ed25519 \| layoutparser_automation> {elson.lopes,elson,Administrator}@172.25.32.42` | `Permission denied (publickey,password,keyboard-interactive)` |
| WinRM (IP) | `New-PSSession -ComputerName 172.25.32.42` | exige TrustedHosts + credencial explícita |
| WinRM (FQDN) | `New-PSSession -ComputerName ndd-not-prd407.nddigital.local` | Kerberos: "Cannot find the computer" |
| SMB | `Test-Path \\172.25.32.42\C$` | `False`; `net view` → erro 1702 (binding handle invalid) |
| RPC | `schtasks /S 172.25.32.42` | "A security package specific error occurred" |

Portas abertas: 22, 3389, 5000 (5985 aberta mas sem trust; 5986 fechada). O rDNS do `.42` é
`ndd-not-prd407.nddigital.local`, mas o forward do nome não resolve e não há trust Kerberos/NTLM
utilizável — a workstation está em `nddigital.local` via VPN (`172.31.44.52`).

**Why:** custa ~10 minutos re-descobrir isso a cada missão que precisa tocar produção. Qualquer
tarefa que exija filesystem, serviço ou tarefa agendada no `.42` está bloqueada até haver credencial.

**How to apply:** antes de planejar trabalho no `.42`, assuma bloqueio e peça UMA das duas coisas
ao dono do projeto:

1. **(preferido)** anexar `C:\Users\elson.lopes\.ssh\id_ed25519.pub` (a **pública**; a privada não
   sai da workstation) em `C:\ProgramData\ssh\administrators_authorized_keys` no `.42`, com ACL
   restrita a `SYSTEM` + `Administrators` — sem isso o `sshd` do Windows ignora o arquivo. Isso
   destrava execução e validação de ponta a ponta.
2. o dono entrar por RDP e rodar os scripts entregues.

Note que o deploy de produção **não** depende disso: o `deploy.yml` roda num runner self-hosted
**dentro** do `.42`, então CI publica normalmente. O bloqueio é só para operação fora do CI.
Ver [[env-gh-cli-ausente]] e [[metrics-job-topology-vm]].
