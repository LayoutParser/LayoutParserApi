---
name: runner-isolation-rollout
description: CI dev agora FAZ deploy (serviço Windows nativo, porta 5100) — criar DEPLOY_PATH_DEV/API_URL_DEV/DB_PASSWORD_DEV no GitHub ANTES do próximo push em feat/**; rotação da senha SQL pendente
metadata:
  type: project
---

Estado 2026-07-18 (fim do dia): isolamento de runners CONCLUÍDO — prod label `production` (WINSRV2022-LIB, org LayoutParser), dev label `dev-local` (NDD-NOT-10910, serviço do runner como LocalSystem — escreve em inetpub e usa SCM/HKLM nos jobs). PR #7 mergeada na master.

`ci-dev.yml` evoluiu de "só build" para build + deploy local de dev: publish Release com `--no-build` (sequência restore→build→publish --no-build validada na máquina), migração automática NSSM→nativo (detecta `nssm` no ImagePath, `sc.exe delete`, recria), serviço Windows NATIVO `LayoutParserApi` (depende do `UseWindowsService()` no Program.cs — mudança do backend-dev ainda NÃO comitada), ambiente do serviço via REG_MULTI_SZ em `HKLM\...\Services\LayoutParserApi\Environment` (`Kestrel__Endpoints__Http__Url` porta 5100 + `Database__Password` do secret, nunca ecoado), smoke test aceita HTTP 200/404.

**Why:** espelho fiel de prod em dev (prod vai migrar de NSSM para serviço nativo com o mesmo mecanismo) e fim da senha SQL em arquivo — houve REGRESSÃO em 2026-07-18 (senha voltou comitada e entrou na master via PR #7; re-sanitizada; **rotação PENDENTE** 🔴).

**How to apply — antes do PRÓXIMO push em `develop`/`feat/**`:**
1. Criar no GitHub (repo Api → Settings → Secrets and variables → Actions): Variable `DEPLOY_PATH_DEV=C:\inetpub\wwwroot\layoutparser`, Variable `API_URL_DEV` (opcional; default `http://localhost:5100`), Secret `DB_PASSWORD_DEV` (senha JÁ rotacionada). Sem `DEPLOY_PATH_DEV` o ci-dev FALHA no guard (proposital). Sem `DB_PASSWORD_DEV` a API sobe degradada (sem SQL) com Write-Warning.
2. Push na master dispara o deploy.yml de PRODUÇÃO — nunca empacotar mudança de CI sem revisar esse efeito colateral.
3. Prod NSSM→nativo: spec entregue no relatório de 2026-07-18, NÃO executada — o deploy.yml de prod segue o antigo (Stop/Start-Service apenas, sem criar serviço).
4. Armadilha de PowerShell aprendida: `binPath= "\"$exe\""` é idiom de cmd — em PS o backslash não escapa; usar `$binPath = '"{0}"' -f $exePath; sc.exe create ... binPath= $binPath`.
