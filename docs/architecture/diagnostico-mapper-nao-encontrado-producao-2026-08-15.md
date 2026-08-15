# Diagnóstico: "Nenhum mapper encontrado" em produção apesar de o mapper existir no banco (2026-08-15)

## Resumo da evidência trazida pelo dono

Log de produção (`2026-08-15 10:45`), endpoint `execute-candidates`, layout
`LAY_TXT_MQSERIES_ENVNFE_4.00_NFe` (`layoutGuid=LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c`):

```
[WRN] Arquivo MAP (TCL) não encontrado: C:\inetpub\wwwroot\layoutparser\TCL\LAY_TXT_MQSERIES_ENVNFE_4.00_NFe.tcl
[WRN] Nenhum mapper encontrado para layoutGuid=LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c nos pacotes permitidos
```

Query manual do dono contra `[ConnectUS_Macgyver].[dbo].[tbMapper]` filtrando só por
`InputLayoutGuid = 'LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c'` **retorna linhas**, incluindo
`Id=470, MapperGuid=MAP_f31a6758-..., PackageGuid=PAC_266bc578-b0fa-48a4-9c72-61004b729576,
ProjectId=2, LastUpdateDate=2026-08-12`. `tbLayout` confirma que o layout existe com o mesmo
`PackageGuid`.

## Achado 1 — os dois warnings vêm de DOIS pathways distintos, na MESMA requisição

`execute-candidates` (`Controllers/TransformationExecutionController.cs:164-199`, comentário na
linha 156-159) dispara **os dois pathways em paralelo/sequência dentro da mesma request**:
"tcl-xsl/canônico" (`TransformationPipelineService.TransformTxtToXmlAsync`, Pathway 2) e
"sysmiddle/low-code" (`LowCodeAutoTransformationService.RunAsync`). O log mostra exatamente essa
sequência — primeiro o warning do TCL ausente (Pathway 2), depois o warning de mapper ausente
(pathway low-code). **Não são a mesma falha nem o mesmo código** — são dois achados independentes
que precisam de diagnóstico separado. Isso não é surpresa nova: já documentado no contrato de
`multi-candidato-e-diagnostico-ia-contrato.md` (Gap 1).

## Achado 2 — o warning "Arquivo MAP (TCL) não encontrado" é comportamento ESPERADO para este layout, não bug

`TransformationPipelineService` (`Services/XmlAnalysis/TransformationPipelineService.cs:28,70-72,387`)
resolve o MAP como `TclPath/{layoutName}.tcl` (fix da issue #39, já mergeado — o código lê
`TransformationPipeline:TclPath`, não uma pasta hardcoded). O caminho no log
(`C:\inetpub\wwwroot\layoutparser\TCL\...`) bate com o `TclPath` configurado no `appsettings.json`
do repositório (`"C:\\inetpub\\wwwroot\\layoutparser\\tcl"` — Windows não diferencia maiúsc./minúsc.
em caminho de pasta, isso não é a causa). **Este layout (`LAY_TXT_MQSERIES_ENVNFE_4.00_NFe`) nunca
teve um `.tcl` legado** — ele é tratado pelo pathway low-code (mapper `MAP_MQSERIES_SEND_ENV_TXT_XML_NFE`
no banco, sem correspondente em `TCL/`). Isso é esperado: nem todo layout tem os dois pathways
disponíveis. **Não é uma pista da causa raiz do segundo warning** — é um "not_applicable" correto
para esse pathway específico.

## Achado 3 — a exceção de SQL NÃO está sendo engolida (fix da issue #38/#39 confirmado no código atual)

`MapperDatabaseService.GetMappersByLayoutGuidForPackagesAsync` (linhas 341-355) tem `catch` que
loga como `LogError` e **relança** (`throw;`), com comentário explícito referenciando a correção da
issue #38/#39 ("NÃO degrada aqui"). Se a query SQL tivesse falhado (timeout, permissão, etc.), a
exceção propagaria e o chamador (`LowCodeAutoTransformationService.TransformAndPersistAsync`)
devolveria um erro distinguível de "sem mapper" — o log mostra exatamente
`"Nenhum mapper encontrado ... nos pacotes permitidos"`, que só é emitido quando `ranked.Count == 0`
(`Services/Transformation/LowCode/LowCodeAutoTransformationService.cs:130-134`), ou seja: **a query
rodou e retornou zero linhas** — não é uma exceção mascarada. Isso descarta a hipótese da issue
#38/#39 recorrer, **desde que o binário em produção corresponda a este código-fonte** (ver Achado 5).

## Achado 4 — causa raiz mais provável: filtro adicional que a query manual do dono NÃO tem

A query real do código (`Services/Database/MapperDatabaseService.cs:306-318`) filtra por **três**
critérios, não só `InputLayoutGuid`:

```sql
WHERE [ProjectId] = @ProjectId
  AND (REPLACE(LOWER([PackageGuid]), 'pac_', '') IN (@p0, @p1, ...))
  AND ([InputLayoutGuid] = @LayoutNoPrefix OR [InputLayoutGuid] = @LayoutWithPrefix
       OR [TargetLayoutGuid] = @LayoutNoPrefix OR [TargetLayoutGuid] = @LayoutWithPrefix)
```

A query que o dono rodou (`SELECT TOP (10) * FROM tbMapper WHERE InputLayoutGuid = '...'`) **não
tem** o filtro de `ProjectId` nem o `IN` de `AllowedPackageGuids`. Os dois adicionais vêm de
`_opt.ProjectId` e `_opt.AllowedPackageGuids`, carregados via `IOptions<LowCodeOptions>` a partir da
seção `LowCode` do `appsettings.json` **do host em execução** — não deste repositório.

Os dados que o dono trouxe (`ProjectId=2`, `PackageGuid=PAC_266bc578-...`) **batem com o que está
neste repositório** (`appsettings.json:121,130` — `ProjectId: 2`,
`PAC_266bc578-b0fa-48a4-9c72-61004b729576` é o primeiro item de `AllowedPackageGuids`). Isso prova
que a config do repositório está correta — **não prova que a config em produção é a mesma**.

Já documentamos (memória `lp-architect/lowcode-nunca-rodou-em-producao.md`, 2026-08-09) que **os
workflows de deploy preservam o `appsettings.json` do servidor de destino** — se o arquivo já existe
lá, ele nunca é sobrescrito pelo deploy (`ci-dev.yml:236-250`, `deploy.yml:394`). Isso significa que
qualquer edição feita no `AllowedPackageGuids`/`ProjectId` do repositório **depois** do primeiro
deploy nunca chega ao servidor — a seção `LowCode` do host de produção pode estar congelada num
estado anterior. Se, por exemplo, `PAC_266bc578-b0fa-48a4-9c72-61004b729576` foi adicionado à lista
*depois* do primeiro deploy, ou se o `ProjectId` mudou, a cláusula `IN`/`ProjectId` do servidor
filtraria a linha 470 mesmo ela existindo e batendo em `InputLayoutGuid`.

**Isto explica os dados apresentados sem exigir bug novo**: a query do dono (sem os dois filtros
extras) encontra a linha; a query da API (com os dois filtros extras, lidos da config do host) não
encontra — porque o host pode estar rodando uma versão diferente (mais antiga) da seção `LowCode`.

## Achado 5 — hipótese concorrente: binário/deploy desatualizado

O fix da issue #38/#39 (troca de `return mappers` engolindo exceção por `throw`) está presente no
código-fonte atual (Achado 3). Mas isso só é relevante se **o binário rodando no host de produção
corresponde a esse commit**. Não temos, nesta investigação, acesso a:
- Data/commit do último deploy real do host onde o log de 10:45 foi gerado;
- O conteúdo atual do `appsettings.json` desse host (seção `LowCode`).

Sem esses dois dados, não é possível diferenciar com certeza entre "config drift" (Achado 4,
mecanismo já comprovado noutro contexto) e "binário anterior ao fix ainda rodando" — mas a segunda
hipótese é **menos provável** aqui porque o warning específico do log
(`"Nenhum mapper encontrado ... nos pacotes permitidos"`) só existe no caminho pós-fix
(`LowCodeAutoTransformationService.cs:132`, que substituiu o comportamento antigo). Se o binário
fosse anterior ao fix #38/#39, o sintoma esperado seria uma exceção SQL sendo relatada como sucesso
vazio, não esse warning textual específico — que já é o comportamento *corrigido*. Isso torna o
Achado 4 (config drift de `AllowedPackageGuids`/`ProjectId`) a hipótese dominante.

## Resposta direta: "como não foi encontrado nada"

A query que a API roda em produção filtra por **`ProjectId` + `AllowedPackageGuids`
(lista de pacotes) além de `InputLayoutGuid`/`TargetLayoutGuid`** — a consulta manual do dono não
tinha esses dois filtros extras. O `ProjectId`/`AllowedPackageGuids` vêm da seção `LowCode` do
`appsettings.json` **do servidor**, que os workflows de deploy **nunca sobrescrevem se o arquivo já
existe lá** (mecanismo documentado em 2026-08-09). Os valores no repositório batem com os dados que
o dono trouxe do banco — mas isso só prova que o repositório está certo, não que o servidor está
com a mesma config. A hipótese mais provável é que a seção `LowCode` do `appsettings.json` em
produção esteja desatualizada (pacote/projeto não incluído na lista que o servidor está realmente
usando), fazendo a query da API devolver zero linhas mesmo com o mapper existindo no banco.

## O que precisa ser confirmado no servidor (ação, não suposição)

1. Ler o `appsettings.json` (ou override via env var `LowCode__AllowedPackageGuids`/
   `LowCode__ProjectId`) **efetivamente carregado pelo processo em produção** — não o do
   repositório. Comparar `ProjectId` e a lista completa de `AllowedPackageGuids` com os valores
   atuais do repositório.
2. Se divergir: corrigir a config do servidor (via env var `Section__Key`, não editando o
   `appsettings.json` do host à mão — ver `.claude/rules/security.md`) e reexecutar
   `execute-candidates` para o mesmo layout.
3. Se **não** divergir (config idêntica): a hipótese de config drift cai, e a investigação
   precisa ir para uma direção que exige acesso ao servidor não disponível aqui — coleção real
   (não `git log` local) de qual commit está rodando (ex.: endpoint de health/versão, se existir,
   ou timestamp do `.dll` no host) — porque nesse caso restaria só a hipótese, menos provável mas
   não descartável, de collation/normalização de string do SQL Server tratando
   `InputLayoutGuid`/`PackageGuid` de forma diferente do que o C# espera (ex.: coluna com espaços
   à direita não capturados pelo `.Trim()` do C#, ou collation case-sensitive fazendo o `REPLACE
   (LOWER(...))` não bater com um `PAC_` gravado em formato inesperado). Isso é apenas plausível a
   partir daqui — não achamos nenhuma evidência de collation nos dados apresentados.

## Delegação

- `@lp-devops`: confirmar a seção `LowCode` do `appsettings.json` efetivo em produção (ação 1) —
  é infraestrutura/config de servidor, fora do escopo de leitura de código.
- `@lp-backend-dev`: se confirmado o config drift, considerar (fora do escopo desta investigação)
  se `AllowedPackageGuids`/`ProjectId` deveriam ser sobrepostos por env var em vez de depender do
  `appsettings.json` congelado do host — mesma classe de risco já registrada para `LowCode:RunnerPath`.

Relacionado: `docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md` (Gap 1, contrato do
`execute-candidates`); memória `lp-architect/lowcode-nunca-rodou-em-producao.md` (mecanismo de
"appsettings do destino nunca é sobrescrito").
