# Clean deploy — estrategia e decisoes (2026-09-07)

## Contexto / pedido

O dono reportou que o deploy (`.github/workflows/deploy.yml` producao e
`.github/workflows/ci-dev.yml` dev) so sobrescreve/atualiza arquivos — nunca limpa o
diretorio de destino. Um arquivo removido/renomeado no repo (ex.: DLL de dependencia
descartada, artefato de build antigo) fica para sempre em `<deploy>\api`, de release em
release. Sintoma concreto usado como gatilho: o `appsettings.json` de producao tem
`Database:Password` em texto plano no disco.

## Investigacao

### 1) Por que a senha do SQL esta em texto plano no disco de producao hoje

**Nao e escrita nova do pipeline nem residuo de edicao manual — e o comportamento
DOCUMENTADO do mecanismo de preservacao do `appsettings.json`.** Achado ja registrado
em `.claude/rules/security.md` (secao 2026-08-15) e confirmado de novo nesta sessao lendo
`deploy.yml`:

- O step "Deploy to server (local)" copia com `-Exclude "appsettings.json"` por padrao —
  o arquivo do servidor nunca e sobrescrito pelo do repo, a menos que
  `vars.MIGRATE_CONFIG_TO_REPO=true` ja tenha rodado com sucesso (`CONFIG_MIGRATED=true`).
- O `appsettings.json` do repo tem `Database:Password: ""` (sem segredo). O do servidor
  tem o valor real, herdado de uma epoca em que a senha era escrita direto no arquivo
  (antes da migracao para env var) e nunca foi limpo porque o arquivo nunca e tocado.
- O comentario `@lp-devops` na issue `#112` (2026-09-06) ja confirmou isso lendo o
  guard-rail do step "Config drift repo x destino": a migracao para
  `appsettings.Production.json` + `Database__Password` no Environment do servico Windows
  esta pronta no codigo, mas nunca foi executada — bloqueada por dois pre-requisitos (ver
  secao seguinte).

**Isso nao e um achado novo desta sessao**, e sim a explicacao factual do sintoma que
motivou o pedido. Nao mudei o comportamento de preservacao do `appsettings.json` em si —
ele continua correto pelo motivo documentado (nao pisar em config local antes da
migracao) —, mas destravei os dois bloqueios que impediam a migracao de rodar.

### 2) Os dois bloqueios da migracao (issue `#112`) — ambos endereçados nesta sessao

1. **Parser do "Config drift" quebrado por comentarios `//` no `appsettings.json`.**
   `ConvertFrom-Json` do PowerShell nao aceita comentario; o `appsettings.json` do repo
   tinha `// ...` nas secoes `Database` e `IdentityDatabase` (commit `0a2162b`). O parse
   falhava silenciosamente (`continue-on-error` + `$ErrorActionPreference = "Continue"`),
   deixando o mapa do repo vazio e o relatorio de drift mentiroso.
   **Corrigido:** os comentarios viraram chaves `"_comment"` (mesmo padrao ja usado na
   secao `Authentication:ServiceClient`) — `appsettings.json` agora e JSON estrito
   (validado com `json.load`). Como defesa em profundidade, o step de config drift em
   `deploy.yml` tambem passou a filtrar linhas `^\s*//` antes do `ConvertFrom-Json`, caso
   um comentario volte a ser introduzido no futuro.
2. **`Database__Password` nunca provisionado no Environment do servico de producao.** O
   guard-rail de segredo do step "Config drift" (deploy.yml) aborta a migracao enquanto
   essa chave divergir do repo sem equivalente no Environment — hoje nao ha secret
   `DB_PASSWORD_PROD`/`managed['Database__Password']` nenhum em `deploy.yml` (so existe o
   equivalente `DB_PASSWORD_DEV` no `ci-dev.yml`, para a instancia de dev — banco
   diferente do de producao).
   **Adicionado:** o step "Configurar ambiente do servico" em `deploy.yml` passou a
   aceitar o secret `DB_PASSWORD_PROD` (upsert, mesma semantica das demais chaves — se o
   secret nao existir, a chave simplesmente nao e gerenciada, nada quebra). **Isto NAO e
   rotacao** — a credencial do SQL `172.31.249.51` continua sendo compartilhada por
   ~231.890 times na NDD e permanece fora de cogitacao para rotacao (ver
   `.claude/rules/security.md`). O secret precisa conter o MESMO valor de senha ja em uso
   hoje. **Acao pendente do dono** (fora do alcance deste agente, sem acesso ao host de
   producao): criar o secret `DB_PASSWORD_PROD` em GitHub → Settings → Secrets and
   variables → Actions, com o valor atual da senha. So depois disso faz sentido reativar
   `vars.MIGRATE_CONFIG_TO_REPO=true`.

Com os dois bloqueios endereçados, a issue `#112` deixa de estar travada por defeito de
mecanismo — o unico passo restante e o dono provisionar o secret com o valor real.

### 3) Diretorios de runtime que a API escreve fora do build (mapeados via `Program.cs` e `appsettings.json`)

| Diretorio (config) | Caminho (producao) | Sobrevive a redeploy? |
|---|---|---|
| `Logging:File:Directory` | `<deploy>\api\logs` | **Sim** — DENTRO de `api\`, precisa de allowlist explicita |
| `backups\` (deploy.yml/ci-dev.yml) | `<deploy>\backups` | Sim — IRMAO de `api\`, fora do escopo da limpeza |
| `ML:LearningDataPath` / `ML:LowCodeTransformationsPath` | `<deploy>\MLData\...` | Sim — movido para fora de `api\` deliberadamente (ver comentario em `deploy.yml`) |
| `Examples:Path` / `Learning:BasePath` | `<deploy>\Exemplo` | Sim — irmao de `api\` |
| `XsdValidation:BasePath` / `PdfBasePath` | `<deploy>\xsd`, `<deploy>\pdf` | Sim — irmao de `api\` |
| `TransformationPipeline:*` (tcl/xsl/Mapeamentro/Examples/LearningModels/ExpectedOutputs) | `<deploy>\tcl`, `\xsl`, etc. | Sim — irmaos de `api\` |
| `LowCode:SysmiddleDir` / `GlobalFolder` | `<deploy>\sysmiddle`, `\globalfolder` | Sim — irmaos de `api\` |
| `LayoutParserDecrypt:Path` / `LowCode:RunnerPath` | `<deploy>\api\LayoutParserDecrypt.exe`, `LayoutParserLowCodeRunner.exe` | Recriado a cada deploy (fazem parte do publish/step de runner) |
| `appsettings.json` / `appsettings.Production.json` | `<deploy>\api\appsettings*.json` | Sim — ja preservado pelo mecanismo existente |

**Conclusao:** quase todo o estado persistente ja vive FORA de `<deploy>\api` (irmaos
como `backups\`, `MLData\`, `Exemplo\`, `xsd\`, `tcl\`, `xsl\`, `sysmiddle\`,
`globalfolder\`). O unico item de estado persistente DENTRO de `api\` — a pasta de logs
(`api\logs`, criada pelo proprio deploy como `api\Logs`) — precisa de allowlist
explicita numa limpeza de `api\`. Isso simplifica MUITO a estrategia: nao ha diretorio de
upload/cache oculto dentro de `api\` que precise de tratamento especial alem de
logs/appsettings.

## Decisao: limpeza seletiva (nao blue-green)

Avaliei duas abordagens:

- **(A) Publicar em staging + swap atomico (blue-green simples)** — mais seguro em
  teoria (destino nunca fica em estado hibrido), mas exige reescrever o mecanismo de
  Stop-Service/rollback/backup que ja existe e ja foi endurecido por incidente real
  (`PR #114`, 2026-08-15 — rollback automatico por falha de smoke test). Trocar essa
  logica testada por um swap de diretorio novo e um risco desproporcional ao problema
  reportado (arquivo orfao acumulado), especialmente em producao.
- **(B) Diff seletivo: remove do destino o que NAO faz parte do publish atual, preservando
  uma allowlist explicita** — mantem toda a maquina de Stop-Service/backup/rollback
  existente intacta; adiciona so um passo de limpeza ANTES da copia. Risco limitado ao
  proprio passo (best-effort, nunca aborta o deploy por falha ao remover um arquivo).

**Escolhida a opcao (B).** Implementada de forma identica nos dois workflows
(`deploy.yml` e `ci-dev.yml`), no step de copia, logo antes de copiar os arquivos novos:

1. Calcula o conjunto de caminhos relativos que o publish atual vai gravar (comparacao
   case-insensitive).
2. Enumera recursivamente os arquivos ja existentes em `<deploy>\api`.
3. Remove qualquer arquivo existente que **nao** esteja no conjunto novo **e nao** esteja
   na allowlist:
   - pasta `Logs`/`logs` inteira (case-insensitive, primeiro nivel) — nunca examinada;
   - `appsettings.json`, `appsettings.Production.json` (producao) — coerente com o
     mecanismo de preservacao ja existente;
   - `appsettings.*.local.json` / `appsettings.Local.json` — mesmo padrao do
     `.gitignore`, caso alguem tenha colocado um manualmente no servidor.
4. Remove diretorios vazios remanescentes (cosmetico).
5. Falha ao remover um arquivo individual e **best-effort** (`Write-Warning`, nao aborta o
   deploy) — o objetivo e reduzir acumulo, nao e um gate.

Depois da limpeza, o restante do fluxo (copia, backup pre-deploy do binario para
rollback, Stop/Start-Service, smoke test de readiness, rollback automatico) continua
**exatamente como estava** — nenhuma dessas partes foi alterada.

## O que NAO mudou (de proposito)

- O mecanismo de preservacao do `appsettings.json` do destino (`-Exclude appsettings.json`
  / `$preserveAppSettings`) continua o mesmo — a limpeza seletiva reforca essa mesma
  politica, nao a substitui.
- Nenhum diretorio irmao de `api\` (`backups\`, `MLData\`, `Exemplo\`, `xsd\`, `tcl\`,
  `xsl\`, `sysmiddle\`, `globalfolder\` etc.) e tocado — a limpeza e escopada a
  `<deploy>\api` apenas.
- O backup pre-deploy do binario atual e o rollback automatico por falha de smoke test
  (mecanismo do incidente `PR #114`) nao foram alterados.

## Riscos residuais

- A limpeza roda **antes** do backup pre-deploy do binario atual em `deploy.yml`? Nao —
  o backup (`Backup pre-deploy do binario atual`) roda ANTES do step "Deploy to server
  (local)" onde a limpeza foi inserida, entao o backup sempre captura o estado anterior
  intacto, incluindo os arquivos que a limpeza esta prestes a remover. Ordem confirmada
  lendo o workflow.
- Se algum servico externo (fora do controle deste repo) gravar arquivos manualmente
  dentro de `<deploy>\api` fora da allowlist (ex.: alguem copiou um `.exe` auxiliar a mao
  para testar), a limpeza vai remove-lo no proximo deploy. Isso e o comportamento
  DESEJADO (destino reprodutivel a partir do repo), mas e uma mudanca de comportamento em
  relacao a hoje — vale avisar quem opera o host.
- `ci-dev.yml` roda num runner de dev com menos supervisao — a mesma logica foi aplicada
  la por consistencia, mas o impacto de um arquivo orfao removido por engano e menor
  (ambiente de dev).

## Issue #112 — status apos esta sessao

Ambos os pre-requisitos que travavam a ativacao de `vars.MIGRATE_CONFIG_TO_REPO=true`
foram endereçados no codigo:

1. Parser do config drift tolerante a comentario + `appsettings.json` do repo corrigido
   para JSON estrito — **feito**.
2. Canal para provisionar `Database__Password` no Environment do servico de producao
   (`secret DB_PASSWORD_PROD`) — **adicionado ao workflow**, mas o VALOR precisa ser
   criado pelo dono (agente nao tem acesso ao host de producao para ler a senha atual).

A ativacao de `MIGRATE_CONFIG_TO_REPO=true` continua sendo uma decisao do dono, a ser
tomada depois de criar o secret `DB_PASSWORD_PROD` — comentario deixado na issue com este
resumo.
