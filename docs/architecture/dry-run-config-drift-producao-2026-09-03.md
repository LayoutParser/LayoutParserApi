# Dry-run "Config drift repo x destino" contra produção — 2026-09-03

Issue: [#110](https://github.com/LayoutParser/LayoutParserApi/issues/110) (B1, origem #108).
Dono: `@lp-devops`.

## Como foi disparado

O step **"Config drift repo x destino (dry-run por padrão)"** (`deploy.yml`, atualmente linhas
~485-648) não tem `workflow_dispatch` isolado — é um step no meio do único job `deploy`
(`runs-on: [self-hosted, windows, production]`), entre "Publicar runner low-code" e "Configurar
ambiente do serviço". Disparar `workflow_dispatch` nesse workflow executa a pipeline de deploy
**inteira** (build, testes, publish, parada do serviço Windows, cópia de binários, restart,
smoke test) — não há como isolar só este step sem editar o workflow.

Por isso, **não foi forçado um deploy novo só para gerar o relatório**. Em vez disso, o
relatório foi lido dos **runs de deploy que já aconteceram naturalmente** via push em `master`
(merge de PR), coisa que já ocorre várias vezes por dia neste repositório. Dois runs relevantes:

- **Run [`33418206427`](https://github.com/LayoutParser/LayoutParserApi/actions/runs/33418206427)**
  (2026-08-31T17:11:23Z, PR #233) — relatório **válido**, é o usado abaixo.
- **Run [`33760825037`](https://github.com/LayoutParser/LayoutParserApi/actions/runs/33760825037)**
  (2026-09-03T13:22:52Z, PR #294, mais recente disponível) — relatório **quebrado** (ver achado
  novo abaixo). Confirmado que todo run de deploy desde 2026-09-01 está quebrado do mesmo jeito
  (checado também o run `33756504024`, 2026-09-03T12:39:55Z).

## Achado novo (não estava em #107/#108): o step está quebrado desde 2026-09-01

O commit `0a2162b` (2026-09-01, "fix(identity): move storage de identidade/workspace para SQL
Server local dedicado") adicionou comentários `//` ao `appsettings.json` do repositório
(linhas 94-98, explicando a seção `IdentityDatabase`). O parser `ConvertFrom-Json` do
PowerShell/Windows **não aceita comentários** (diferente do parser de config do .NET, que tolera
`// `/`/* */` via `JsonDocumentOptions`). Resultado: `Flatten (Get-Content $repo -Raw |
ConvertFrom-Json)` falha com `ArgumentException`, `$mapaRepo` fica vazio (`Chaves no repo: 0`), e
o relatório de todo run de deploy a partir de 2026-09-01 mostra **as 28 chaves do destino
inteiras** como "só no destino" — um falso positivo total, não um diff real.

**Impacto:** o dry-run está inoperante em produção há 3 dias corridos (2026-09-01 a 2026-09-03),
rodando silenciosamente (o step tem `continue-on-error: true`, então nunca falhou o deploy nem
chamou atenção). Precisa de correção — não decidido aqui (fora do escopo desta issue, que é só
leitura). Duas opções óbvias para quem for corrigir: (a) remover os comentários do
`appsettings.json` do repo (viola o padrão de comentário PT-BR já estabelecido no projeto, mas
JSON estrito não suporta comentário), ou (b) trocar `ConvertFrom-Json` por um parser tolerante a
comentários no step do workflow (ex.: `System.Text.Json` via `JsonDocumentOptions
{ CommentHandling = Skip }` chamado de dentro do PowerShell, ou pré-processar removendo linhas
`//` antes do `ConvertFrom-Json`). **Recomendação: abrir issue nova para o `@lp-pm` formalizar.**

## Relatório de drift válido (run `33418206427`, 2026-08-31T17:11:23Z)

`Chaves no repo: 78 | no destino: 28`

### [1] Só no REPO — nunca chegaram ao servidor, código usa o default (55 chaves)

Confirma e amplia a lista já conhecida de #107/#108 (que citava só `LowCode`/`Ollama` como
sintoma pontual). O drift real cobre **8 seções inteiras** nunca publicadas:

- **`LowCode:*`** (11 chaves — já mapeado em #107, causa raiz do incidente `2026-08-15`)
- **`XsdValidation:*`** (11 chaves — validação de schema NFe/CTe/NFCom/MDFe inteira nunca chegou
  ao destino; código cai no default, que não valida nada)
- **`TransformationPipeline:*`** (10 chaves — paths de TCL/XSL/exemplos/modelos de aprendizado)
- Chaves avulsas em outras seções: `AiMetrics:IngestApiKey`,
  `AiTransformationCandidate:{CleanupIntervalMinutes,MaxIterations,SanityTimeoutMinutes,TicketTtlHours}`,
  `Cors:AllowedOrigins`, `Database:{CommandTimeout,ConnectionTimeout,Encrypt}`, `Examples:Path`,
  `Kestrel:Endpoints:Https:Url`, `LayoutValidation:{DailyValidationTime,InitialValidationLayouts}`,
  `Learning:BasePath`, `Logging:File:{FileSizeLimitKB,RetainedFileCountLimit}`,
  `ML:{LearningDataPath,LowCodeTransformationsPath}`, `Ollama:DiagnosisTimeoutSeconds`,
  `Security:{TrustedRolesHeader,TrustedUserHeader}`, `StructuralResolution:*`,
  `TransformationRules:Path`.

Todas usam o default do C# em produção hoje — o `ProjectId=2` citado em #108 como exemplo de
"default que coincide por acidente" é só um caso dentro de `LowCode:*`.

### [2] DIVERGENTES — destino tem valor próprio (3 chaves)

| Chave | Repo | Destino |
|-------|------|---------|
| `Database:Password` | (vazio) | *(omitido — chave sensível)* |
| `Kestrel:Endpoints:Http:Url` | `http://127.0.0.1:5000` | `http://0.0.0.0:5000` |
| `RAG:ExamplesPath` | `Exemplos` | `***\Examples` (path local) |

`Kestrel:Endpoints:Http:Url` é o mais relevante: o repo já reflete o bind loopback-only decidido
em `.claude/rules/security.md` (2026-08-12, remoção do `ApiKeyGateFilter`), mas **produção ainda
está com bind `0.0.0.0`** — a divergência confirma que esse endurecimento de rede nunca chegou ao
`.42` porque o deploy preserva o `appsettings.json` do host. Isso já era suspeitado (memória
`lp-devops/rede-loopback-e-apikey-removido.md`: "não 100% confirmado") — **agora está confirmado
pelo dado real do drift**.

### [3] Só no DESTINO — órfã/removida do repo (11 chaves)

`ElasticSearch:{Password,Url,Username}`, `Gemini:{ApiKey,Model}`, `Logging:Txt:{CustomDirectory,
Enabled,FileName}`, `Logging:Type`, `OpenAI:{ApiKey,ApiUrl}` — todos **subsistemas mortos**
(código já removido do repo), confirmando o que `.claude/rules/security.md` já documentava:
produção ainda carrega `Gemini:ApiKey`/`OpenAI:ApiKey`/`ElasticSearch:Password` em texto plano no
disco, sem consumidor no código atual. Nada novo aqui além da confirmação — já rastreado como
pendência de hardening.

## Resumo para a issue #110

- Critério de aceite "confirmado/refutado que `LowCode` é a única seção órfã": **refutado** — são
  8 seções inteiras (55 chaves) que nunca chegaram a produção, não só `LowCode`. Ver [1] acima.
- Achado extra de risco médio: `Kestrel:Endpoints:Http:Url` diverge — produção ainda escuta em
  `0.0.0.0`, não `127.0.0.1` como o repo já assume desde 2026-08-12.
- Achado extra (bug no próprio mecanismo de diagnóstico): o step está quebrado desde 2026-09-01
  por comentários `//` no `appsettings.json` — precisa de correção antes de confiar em qualquer
  relatório gerado a partir de agora.
- Nenhum deploy foi forçado; os dados vêm de runs de deploy que já ocorreram naturalmente via
  merge de PR (push em `master`), preservando a regra de não tocar produção sem necessidade.
