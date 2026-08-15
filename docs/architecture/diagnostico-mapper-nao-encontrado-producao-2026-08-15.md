# Diagnóstico: "Nenhum mapper encontrado" em produção apesar de o mapper existir no banco (2026-08-15)

## Status: CAUSA RAIZ CONFIRMADA (atualizado com o `appsettings.json` real de produção)

A hipótese do Achado 4/5 abaixo (config drift na seção `LowCode` do host) foi **confirmada** com
evidência concreta: o dono colou o `appsettings.json` real, atualmente carregado em produção. A
seção **`LowCode` inteira está ausente** do arquivo — não é um valor desatualizado, é a chave
`LowCode` que não existe. Mecanismo exato e correção estão nas seções novas ao final deste
documento (**"Confirmação com o `appsettings.json` real de produção"** em diante). O restante do
documento (Achados 1-5) é preservado como registro da investigação original.

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

## Achado 4 — causa raiz mais provável (na época): filtro adicional que a query manual do dono NÃO tem

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
`_opt.ProjectId` e `_opt.AllowedPackageGuids`, carregados via `IOptions<LowCodeRunnerOptions>` a
partir da seção `LowCode` do `appsettings.json` **do host em execução** — não deste repositório.

Os dados que o dono trouxe (`ProjectId=2`, `PackageGuid=PAC_266bc578-...`) **batem com o que está
neste repositório** (`appsettings.json:121,130` — `ProjectId: 2`,
`PAC_266bc578-b0fa-48a4-9c72-61004b729576` é o primeiro item de `AllowedPackageGuids`). Isso prova
que a config do repositório está correta — **não prova que a config em produção é a mesma**.

Já documentamos (memória `lp-architect/lowcode-nunca-rodou-em-producao.md`, 2026-08-09) que **os
workflows de deploy preservam o `appsettings.json` do servidor de destino** — se o arquivo já existe
lá, ele nunca é sobrescrito pelo deploy (`ci-dev.yml:236-250`, `deploy.yml:394`). Isso significa que
qualquer edição feita no `AllowedPackageGuids`/`ProjectId` do repositório **depois** do primeiro
deploy nunca chega ao servidor.

## Achado 5 — hipótese concorrente (na época): binário/deploy desatualizado

Descartada pelo mesmo raciocínio do Achado 3: o warning textual específico do log só existe no
caminho pós-fix #38/#39. Se o binário fosse anterior ao fix, o sintoma esperado seria uma exceção
SQL mascarada, não esse warning. Isso já apontava o Achado 4 (config drift) como hipótese
dominante — agora confirmado.

---

## Confirmação com o `appsettings.json` real de produção (2026-08-15, atualização)

O dono colou o conteúdo real e atual do `appsettings.json` carregado pelo processo em produção.
Evidência decisiva: **a chave `"LowCode"` não existe em lugar nenhum do JSON** — não é um valor
antigo, a seção inteira nunca foi escrita nesse host (reforça o mecanismo já documentado: o deploy
preserva o `appsettings.json` pré-existente do destino, e este host nunca teve a seção `LowCode`
adicionada a ele).

### Mecanismo exato: binding de `IOptions<LowCodeRunnerOptions>` com seção ausente

`Program.cs:464` registra:

```csharp
builder.Services.Configure<LowCodeRunnerOptions>(builder.Configuration.GetSection("LowCode"));
```

`IConfiguration.GetSection("LowCode")` **nunca lança exceção quando a chave não existe** — devolve
um `IConfigurationSection` "vazio" (existe, mas sem filhos). O binder do `Options` pattern, ao
encontrar uma seção sem filhos, simplesmente **não seta nenhuma propriedade** — o objeto final é a
instância criada pelo construtor de `LowCodeRunnerOptions` com **todos os seus valores default do
C#**, não os defaults "razoáveis" que alguém poderia assumir olhando o `appsettings.json` do repo.

Os defaults relevantes (`Services/Transformation/LowCode/LowCodeRunnerOptions.cs`):

| Propriedade | Default em C# (sem config) | Efeito |
|---|---|---|
| `ProjectId` | `2` | **Coincide** com o `ProjectId=2` do mapper `Id=470` no banco — por isso este campo **não** é o culpado, apesar de também estar ausente do JSON. |
| `AllowedPackageGuids` | `new()` → lista **vazia** | **Este é o culpado.** |
| `Package` | `""` | Vazio faz o runner (`.exe`) sair com `exit=9` (`RunnerExitCodes.PackageNotConfigured`) — quebra um caminho de execução diferente (invocação do runner), não a query de mapper. |
| `RunnerPath`, `SysmiddleDir`, `GlobalFolder` | `""` | Também vazios — todo o subsistema low-code está sem config funcional neste host, não só a query. |

Com `AllowedPackageGuids` vazio, a query em `MapperDatabaseService.GetMappersByLayoutGuidForPackagesAsync`
(linhas 300-304) monta:

```csharp
var allowedNorm = new HashSet<string>(allowedPackageGuids.Select(NormalizePackageGuid), ...); // vazio
var pkgParams = allowedNorm.Select((_, i) => $"@p{i}").ToList();                               // vazio
var inClause = pkgParams.Count > 0 ? string.Join(", ", pkgParams) : "NULL";                    // "NULL"
```

O `WHERE` efetivo em produção vira:

```sql
WHERE [ProjectId] = 2                                   -- bate (coincidência de default, não config real)
  AND (REPLACE(LOWER([PackageGuid]), 'pac_', '') IN (NULL))  -- NUNCA verdadeiro para nenhuma linha
  AND (...)
```

`x IN (NULL)` é sempre `UNKNOWN`/falso em SQL, para qualquer valor de `x` — mesmo se o `PackageGuid`
do mapper `Id=470` estivesse correto. **A query roda sem erro, não lança exceção (por isso o Achado
3 continua válido — não há exceção mascarada), e devolve zero linhas sempre, para qualquer layout,
independentemente de qual mapper exista no banco.** Isso bate exatamente com o log
(`"Nenhum mapper encontrado ... nos pacotes permitidos"`, emitido quando `ranked.Count == 0`).

**Correção à hipótese original:** o Achado 4 especulava "config drift" genérico em `ProjectId`
*e* `AllowedPackageGuids`. Com o dado real, `ProjectId` não é o problema (o default do C# já
coincide com o valor do banco, por acaso). O bloqueio é **exclusivamente** `AllowedPackageGuids`
vazio — mas o efeito é o mesmo: zero mappers encontrados para qualquer layout/pacote, não só o
`LAY_TXT_MQSERIES_ENVNFE_4.00_NFe` deste ticket.

### Chaves `LowCode:*` que faltam em produção (valores de referência do repositório, não-sensíveis)

Do `appsettings.json` do repositório (linhas 115-126), campos que não são segredo:

```json
"LowCode": {
  "RunnerPath": "C:\\inetpub\\wwwroot\\layoutparser\\api\\LayoutParserLowCodeRunner.exe",
  "SysmiddleDir": "C:\\inetpub\\wwwroot\\layoutparser\\sysmiddle",
  "GlobalFolder": "C:\\inetpub\\wwwroot\\layoutparser\\globalfolder",
  "Package": "938f9978-836f-48c1-9c0f-c2898caf4b20",
  "ProjectId": 2,
  "AllowedPackageGuids": ["PAC_266bc578-b0fa-48a4-9c72-61004b729576", "..."],
  "MultiCandidateTopN": 4,
  "RunnerTimeoutSeconds": 180,
  "MaxConcurrentRunners": 2,
  "SyncDeliveryTimeoutSeconds": 6,
  "CandidatesRequestTimeoutSeconds": 90,
  "InlineXmlMaxChars": 262144,
  "TransformationCacheTtlHours": 2
}
```

Nenhum desses valores é segredo — são caminhos de instalação e parâmetros operacionais, seguros
para ir em `appsettings.json` ou variável de ambiente sem cuidado especial de rotação. O único item
que exige atenção é conferir se `AllowedPackageGuids` do repo está **completo** (todos os pacotes
que produção de fato usa) antes de aplicar — não assumir que a lista do repo já é exaustiva.

---

## Achado novo — `Ollama:Url` órfão apontando para `localhost`, host real é uma VM Linux separada

O dono reportou que, em produção, `Ollama:Url` = `http://localhost:11434`, mas o Ollama **não roda
mais no host Windows Server 2022** — roda numa VM Linux, e o Windows Server é o **hospedeiro** dessa
VM, não a própria VM. `localhost` do processo ASP.NET Core aponta para o Windows Server, não para a
VM — a URL está estruturalmente errada para o topologia atual (mesma classe de drift do achado
principal: configuração desatualizada em relação à infraestrutura real, documentada em
`.claude/agent-memory/lp-architect/deploy-production-topology.md`).

**Isto é um gate ativo quebrado agora, não só uma pendência de limpeza.** Confirmado em código:

- `Program.cs:367` registra `Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"))`
  e `Program.cs:374` registra `AddHttpClient<OllamaValidationDiagnosticService>` — **ambos ativos
  no DI de produção**, não código morto (diferente do caso do Gemini/OpenAI, já decomissionados e
  não registrados).
- `OllamaValidationDiagnosticService` (`Services/XmlAnalysis/OllamaValidationDiagnosticService.cs`)
  é o serviço de diagnóstico de erro de validação (Gap 2 do contrato multi-candidato) e depende de
  alcançar `_options.Url` para funcionar.
- Isso está alinhado com a decisão de arquitetura já registrada (memória
  `lp-architect/gemini-openai-decommission-decision.md`): Gemini/OpenAI foram abandonados e
  **Ollama local assume 100% do papel de LLM** — não há fallback para nuvem. Se o Ollama é
  inalcançável, o diagnóstico via IA simplesmente não roda em produção **hoje**.

**Mas o serviço degrada corretamente** — segue o princípio de resiliência do projeto
(`.claude/rules/dotnet-standards.md`): connection refused é capturado e logado como
`LogWarning("Ollama indisponível em {Url}", _options.Url)` (linha 89), sem derrubar o request
principal. Ou seja: **não é um crash**, é uma feature (diagnóstico assistido por IA) silenciosamente
sempre indisponível — o request principal de parse/transformação continua respondendo normalmente,
só sem o diagnóstico enriquecido. Isso é o comportamento correto do padrão de resiliência, mas
esconde o problema: sem olhar o log, ninguém percebe que a feature nunca funcionou desde que o
Ollama migrou para a VM.

**Correção:** `Ollama:Url` precisa apontar para o IP/host da VM Linux (porta `11434`), não
`localhost`. Requer confirmar com `@lp-devops` o endereço de rede atual da VM (não documentado
neste arquivo para evitar valor desatualizado — a topologia já registrada em
`deploy-production-topology.md` confirma a separação host/VM, mas não fixa o IP).

---

## Achado novo — `ElasticSearch:Password: "123"` em texto claro — resquício órfão, não risco ativo

O `appsettings.json` de produção ainda tem a seção `ElasticSearch` com `Username: "elastic"` e
`Password: "123"` em texto plano. Verificado no código atual: **não há mais nenhum consumidor**
dessa seção.

```
grep -rn "ElasticSearch" *.cs → sem resultados de código de produção ativo
```

Isso confirma o que `.claude/rules/security.md` já registra: o mecanismo Elastic **nunca foi
conectado ao pipeline real** (Serilog é o logging efetivo) e o código morto (`ILoggingStrategy`,
`ElasticSearch*`) foi removido do repositório em 2026-07-27. A seção que sobra no `appsettings.json`
de produção é órfã do lado da config — nada no binário atual a lê.

**Classificação:** não é um risco ativo (nenhum código consome essas credenciais hoje), mas é uma
senha fraca (`"123"`) em texto plano num arquivo de config de produção, e merece ser removida por
higiene — mesma lógica de "não deixar segredo morto no arquivo só porque não é explorável agora"
já aplicada à chave do Gemini. Diferença importante: a chave do Gemini precisa ser **revogada no
provedor** (ação fora do terminal); aqui não há provedor a revogar — é só remover as duas linhas do
`appsettings.json` de produção (ou zerar via env var, se preferir não editar o arquivo à mão).

---

## Runbook de correção (produção)

Seguindo o padrão já documentado em `.claude/rules/security.md` para `Database`/`Gemini`
(precedência `appsettings.json` → env vars `Section__Key`, sem editar o `appsettings.json` do host
à mão): configurar as variáveis de ambiente do serviço Windows
(`HKLM\SYSTEM\...\Services\LayoutParserApi\Environment`, mesmo mecanismo do `ci-dev.yml` para
`DB_PASSWORD_DEV`) com:

```
LowCode__RunnerPath=C:\inetpub\wwwroot\layoutparser\api\LayoutParserLowCodeRunner.exe
LowCode__SysmiddleDir=C:\inetpub\wwwroot\layoutparser\sysmiddle
LowCode__GlobalFolder=C:\inetpub\wwwroot\layoutparser\globalfolder
LowCode__Package=938f9978-836f-48c1-9c0f-c2898caf4b20
LowCode__ProjectId=2
LowCode__AllowedPackageGuids__0=PAC_266bc578-b0fa-48a4-9c72-61004b729576
LowCode__AllowedPackageGuids__1=<demais pacotes permitidos, conferir lista completa do repo>
Ollama__Url=http://<ip-da-vm-linux>:11434
```

(`AllowedPackageGuids` é uma lista — o binder do `IConfiguration` para env vars usa índice
numérico `__0`, `__1`, ... como sufixo, não uma string separada por vírgula.)

Passos:

1. Confirmar com `@lp-devops` o IP/host atual da VM Linux que roda o Ollama (não assumir — a
   topologia mudou e este documento não fixa um valor que pode já estar desatualizado de novo).
2. Aplicar as variáveis de ambiente acima no serviço Windows de produção (mesmo runbook de rotação
   já documentado em `rules/security.md`, seção "Segredos no CI de dev").
3. Reiniciar o serviço `LayoutParserApi` para o `IOptions` reler o ambiente atualizado.
4. Reexecutar `execute-candidates` para `layoutGuid=LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c` e
   confirmar que o mapper `Id=470` é encontrado (warning "Nenhum mapper encontrado" desaparece).
5. Confirmar no log que o diagnóstico via Ollama deixa de emitir `"Ollama indisponível em
   http://localhost:11434"` e passa a responder (ou falhar por outro motivo, mas alcançando a VM).
6. Separadamente (não bloqueante, não relacionado ao bug do mapper): remover
   `ElasticSearch:Username`/`ElasticSearch:Password` do `appsettings.json` de produção — item de
   limpeza de higiene, sem consumidor no código atual.
7. **Item preexistente, não introduzido por este diagnóstico:** o `ProjectId=2` "funcionar por
   coincidência de default" é frágil — se o schema do banco algum dia introduzir `ProjectId != 2`
   como válido para outro projeto, o mesmo host voltaria a quebrar silenciosamente sem que a causa
   fosse óbvia (default do C# mascarando ausência de config). Vale considerar, fora do escopo desta
   correção imediata, se `LowCodeRunnerOptions.ProjectId` deveria ter um default inválido
   (ex.: `0` ou `-1`) para falhar de forma mais visível quando a seção `LowCode` estiver ausente —
   decisão de `@lp-backend-dev`/`@lp-architect`, não implementada aqui.

## Delegação

- `@lp-devops`: aplicar as variáveis de ambiente `LowCode__*` e `Ollama__Url` em produção (runbook
  acima) — é infraestrutura/config de servidor, fora do escopo de leitura de código. Confirmar o
  IP da VM Linux do Ollama antes de aplicar.
- `@lp-backend-dev`: avaliar (fora do escopo desta correção imediata) se `LowCodeRunnerOptions`
  deveria falhar de forma mais visível (ex.: validação no startup, ou `ProjectId` default inválido)
  quando a seção `LowCode` estiver ausente, em vez de silenciosamente rodar com defaults que podem
  coincidir por acidente com um valor válido do banco.

Relacionado: `docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md` (Gap 1/Gap 2, contrato
do `execute-candidates`); memória `lp-architect/lowcode-nunca-rodou-em-producao.md` (mecanismo de
"appsettings do destino nunca é sobrescrito"); memória `lp-architect/deploy-production-topology.md`
(separação host Windows Server / VM Linux do Ollama); `.claude/rules/security.md` (padrão de env
vars `Section__Key`, status da remediação de segredos).
