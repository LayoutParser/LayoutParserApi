---
name: execute-candidates-ausencia-total-para-cnhi-envnfe
description: Diagnóstico completo de candidates:[] para LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe — 3 causas independentes, nenhuma corrigível só com código deste repo
metadata:
  type: project
---

Investigação de 2026-08-12 sobre `POST /api/transformationexecution/execute-candidates` devolvendo
`candidates: []` para `LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe`. Três causas **independentes**, cada
uma suficiente sozinha para zerar o resultado:

1. **Pathway sysmiddle** (`ExecuteSysmiddleCandidatesAsync`, `TransformationExecutionController.cs:272`):
   `Applicable=false` vem de `GetMappersByLayoutGuidForPackagesAsync`
   (`Services/Database/MapperDatabaseService.cs:284`) devolver 0 linhas do SQL
   `[ConnectUS_Macgyver].[dbo].[tbMapper]`. Filtro é `ProjectId=2` (config `LowCode:ProjectId`) **E**
   `PackageGuid` dentro de `LowCode:AllowedPackageGuids` (13 GUIDs em `appsettings.json`). Achado
   suspeito, **não confirmado por falta de acesso SQL neste ambiente**: `LowCode:Package`
   (`938f9978-836f-48c1-9c0f-c2898caf4b20`, usado como `--package` do runner CLI, ver
   [[gabarito-fiat-comando-de-verificacao]]) **não aparece** em nenhum dos 13
   `AllowedPackageGuids`. Não são necessariamente o mesmo conceito (`Package` = projeto Sysmiddle
   inteiro; `AllowedPackageGuids` = subconjunto de pacotes elegíveis dentro dele), então isso é
   pista, não prova — precisa de `SELECT PackageGuid FROM tbMapper WHERE InputLayoutGuid/TargetLayoutGuid
   = <guid de LAY_CNHI...>` para confirmar. Mesmo padrão de lacuna documentado para a Marelli em
   `docs/architecture/spec-fase3-fase4-gate-transformacao-e-dataset.md` (P4).

2. **Pathway tcl-xsl** (`TransformationPipelineService.LoadMappingFileAsync:371`): procura
   `MAP_{layoutName}.xml` (ex.: `MAP_LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.xml`) dentro de
   `TransformationPipeline:MappingPath` (`C:\inetpub\wwwroot\layoutparser\Mapeamentro`), com fallback
   hardcoded para `MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.xml`. **Nenhum dos dois nomes bate com a
   convenção real**: o dump do servidor de produção (`.claude/tmp/servidor/layoutparser/Examples/`)
   tem uma pasta `MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE` (sem `.xml`, sem o nome do layout, dentro
   de `Examples`, não de `Mapeamentro`) — e a pasta `Mapeamentro` **nem aparece** no dump. Isso é
   estrutural: o pathway tcl-xsl parece **nunca ter funcionado para nenhum layout real** (não é bug
   específico deste layout), mas eu não tenho como confirmar 100% sem acesso ao filesystem de
   produção. Não tentei "adivinhar" a convenção certa e corrigir `LoadMappingFileAsync` — o mesmo
   tipo de aposta errada já custou um ciclo da arquiteta (mapper `MAP_MARELLI_` homônimo, ver
   [[gabarito-fiat-comando-de-verificacao]]). Correção real é do dono do artefato (ops/Sysmiddle),
   não deste repo.

3. **Pathway IA/Ollama (terceiro pathway, esperado pelo dono do projeto): não existe wiring nenhum**
   em `execute-candidates`. `AutoTransformationGeneratorService` é injetado no construtor do
   `TransformationExecutionController` mas **nunca é chamado** em lugar nenhum do arquivo — e mesmo
   que fosse, é um gerador **baseado em regras** (`TclGeneratorService`/`XslGeneratorService`), não
   Ollama. O único serviço que fala com Ollama na API,
   `Services/XmlAnalysis/OllamaValidationDiagnosticService.cs`, é **diagnóstico de erro de validação**
   (sugestão de fix textual), não geração de XSLT/TCL, e também não é chamado por este endpoint. A
   geração real via LLM (loop RAG gerar→validar→corrigir) só existe como CLI offline
   (`ai/XslSynth/Program.cs`), fora do processo da API. Ou seja: não é que a IA "tentou e falhou" —
   ela nunca foi invocada nesta chamada porque o endpoint não tem esse pathway implementado.

**Contagem esperada de candidatos:** não determinável com certeza sem SQL (item 1). Teto de design é
`LowCode:MultiCandidateTopN=4` (sysmiddle) + no máximo 1 (tcl-xsl, hoje sempre 0 na prática) + 0 (IA,
não implementado neste endpoint) = até 5 no melhor caso, mas o valor real depende de quantos mappers
distintos existem em `tbMapper` para o `InputLayoutGuid`/`TargetLayoutGuid` deste layout dentro do
`ProjectId`/pacotes permitidos — pergunta em aberto, não fechada nesta sessão.

**Why isso não virou fix de código:** as 3 causas são todas externas ao que este repo pode corrigir
sem risco: (1) é config/dado de outro sistema (SQL `tbMapper` + `AllowedPackageGuids`, decisão de
allowlist que pode ser deliberada), (2) é convenção de artefato de outro sistema (Sysmiddle/ops), (3)
é gap de arquitetura conhecido (RAG/Ollama nunca foi conectado a este endpoint) que é decisão de
escopo do `@lp-architect`/dono do projeto, não um bug de uma linha.

**How to apply:** antes de qualquer fix futuro aqui, (a) rodar a query SQL do item 1 pra confirmar/
descartar o mismatch de PackageGuid; (b) confirmar com ops a convenção real de nome/pasta do MAP para
decidir se vale reescrever `LoadMappingFileAsync` ou aposentar o pathway tcl-xsl de vez (ele parece
morto pra qualquer layout, não só este); (c) se o dono do projeto quiser o pathway de IA neste
endpoint, é uma feature nova (design + implementação), não uma correção de bug — encaminhar como PBI
via `@lp-pm`.

## Capítulo 2 (2026-08-12) — SQL do item 1 rodado, PackageGuid REFUTADO como causa

Dono rodou `SELECT PackageGuid, MapperGuid, Name FROM tbMapper WHERE InputLayoutGuid =
'LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1'` (o `LAY_...` correto, já confirmado). Resultado: 2
mappers reais (`MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE` e `MAP_CNHI_MQSERIES_RET_LOGTRACE_TXT_TXT_NFE_3.1`),
ambos com `PackageGuid = PAC_36f1d551-06fb-4abc-80cc-aa565f4a258e` — que **está** em
`LowCode:AllowedPackageGuids` (`appsettings.json:126`). **Hipótese 1 original (mismatch de
PackageGuid) está refutada.** Existem dados reais e elegíveis; o `candidates: []` é código não
achando dado que existe.

Reabri a investigação de código com 3 checagens, todas em
`Services/Database/MapperDatabaseService.GetMappersByLayoutGuidForPackagesAsync`
(`MapperDatabaseService.cs:284`) e no fluxo de resolução de GUID em
`Controllers/TransformationExecutionController.cs`:

1. **`ProjectId` — suspeito mais provável, AINDA NÃO CONFIRMADO.** A query do dono não trouxe
   `ProjectId`, e o SQL real tem `WHERE [ProjectId] = @ProjectId` (linha 312) além do filtro de
   package/layout. `LowCode:ProjectId = 2` (`appsettings.json:115`). Se os 2 mappers achados
   tiverem `ProjectId` diferente de 2, a query devolve 0 linhas mesmo com PackageGuid e
   InputLayoutGuid corretos — exatamente o sintoma observado. **Próxima query exata pro dono
   rodar:**
   ```sql
   SELECT ProjectId, PackageGuid, MapperGuid, Name
   FROM [ConnectUS_Macgyver].[dbo].[tbMapper]
   WHERE InputLayoutGuid = 'LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1'
   ```
   Se `ProjectId <> 2` nas duas linhas, é a causa raiz confirmada — mas a correção **não é
   trivial**: mudar `LowCode:ProjectId` no `appsettings.json` é decisão de escopo (pode haver
   motivo pra filtrar só o projeto 2), não um bug de uma linha. Encaminhar ao dono/`@lp-architect`
   antes de tocar em config.

2. **Resolução de `layoutGuid` quando vem vazio no request — REFUTADO como causa.** Li
   `TransformationExecutionController.cs:154-260` e `Services/Transformation/LowCode/
   LowCodeLayoutGuidResolver.cs`. O controller resolve o layout por **nome**
   (`_layoutDb.SearchLayoutsAsync` filtrando por `request.LayoutName`, linha 174-179) e obtém
   `layoutRecord.LayoutGuid` do catálogo — não depende de o front-end mandar `LayoutGuid` no
   payload. `LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid)`
   (chamado na linha 284) já tem fallback: usa o GUID do request só se for um GUID válido
   (com/sem prefixo `LAY_`), senão cai no GUID do catálogo; só devolve `null` (e aborta o
   pathway sysmiddle com warning) se **nenhum dos dois** for válido. Isso já está coberto por
   4 testes de regressão existentes em
   `tests/LayoutParserApi.Tests/Transformation/LowCodeLayoutGuidResolverTests.cs` — não achei
   lacuna nova aqui. O GUID efetivamente usado na chamada a `GetMappersByLayoutGuidForPackagesAsync`
   é sempre um GUID válido (do request OU do catálogo), nunca vazio — e o build anterior já
   confirmou que esse GUID bate com o `InputLayoutGuid` usado na query do dono.

3. **Filtro silencioso pós-query (`IsXPathMapper`, decriptação silenciosa) — REFUTADO.**
   `IsXPathMapper` é lido do reader (`MapperDatabaseService.cs:378`) mas **não é usado como
   filtro em lugar nenhum** do arquivo (`grep` não achou nenhum `if`/`Where` sobre esse campo) —
   é só um campo informativo no objeto `Mapper`. A suspeita de decriptação silenciosa também
   é refutada: `MapReaderToMapperAsync` (linha 351-406) captura `DecryptionException`, loga
   Warning e segue com `mapper.DecryptedContent = ""` — **o mapper continua na lista**, não é
   descartado. `ExtractLayoutGuidsFromDecryptedContent` (linha 411+) só enriquece
   `InputLayoutGuid`/`TargetLayoutGuid` a partir do XML se a coluna do banco já não tiver
   valor; não filtra nada. Nenhum dos dois caminhos explica `candidates: []` com PackageGuid
   e InputLayoutGuid corretos.

**Conclusão do capítulo 2:** só com código, refutei os itens 2 e 3 como causa — o bug não está
na resolução de GUID nem em filtro pós-query. A única hipótese viva é o `ProjectId`, e ela
depende do SQL acima. **Nenhum código foi alterado nesta sessão** — não há fix pra propor sem
esse dado, e forçar uma mudança em `LowCode:ProjectId` às cegas repetiria o erro já registrado
em [[gabarito-fiat-comando-de-verificacao]] (aposta errada em convenção sem confirmação).

## Capítulo 3 (2026-08-12) — ProjectId REFUTADO; achado real: exceção de SQL virava `Applicable=false` silencioso

Dono rodou a query final (`SELECT ProjectId, PackageGuid, MapperGuid, Name FROM tbMapper WHERE
InputLayoutGuid = 'LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1'`). Resultado: as 2 linhas têm
`ProjectId=2`, batendo com `LowCode:ProjectId`. **Hipótese do `ProjectId` refutada** — não sobra
nenhuma hipótese de mismatch de dado/config nos filtros do WHERE (ProjectId, PackageGuid,
InputLayoutGuid todos conferem). Com as 3 causas do capítulo 1 e as 2 do capítulo 2 refutadas,
o SQL do dono e o SQL do código deveriam devolver o mesmo resultado — e não devolvem.

**Hipótese A (connection string diferente) — não confirmável, mas SEM evidência de divergência.**
Comparei `MapperDatabaseService` e `LayoutDatabaseService`
(`Services/Database/MapperDatabaseService.cs:20-33`, `Services/Database/LayoutDatabaseService.cs:17-45`):
os dois leem as MESMAS chaves de config (`Database:Server/Database/UserId/Password`) da MESMA
fonte (`IConfiguration` — `appsettings.json` → user-secrets → env vars). Não há dois bancos
diferentes configurados no código deste repo; se há drift, é **fora do repo** (ex.: variável de
ambiente do serviço Windows configurada errado só em produção — sem acesso, não dá pra confirmar
nem descartar). Diferença real entre as duas classes: `MapperDatabaseService` não tem fallback
hardcoded pros valores de config (linha 27-30, sem `??`), `LayoutDatabaseService` tem
(`?? "172.31.249.51"` etc., linha 26-30) — se algum dia uma chave sumir do ambiente só pra uma
das duas, elas se comportam diferente (uma cai no default, a outra vira `Server=;...` e falha a
conexão). Não encontrei evidência de que isso esteja acontecendo agora, só documentei a
assimetria.

**Achado real (código, corrigido nesta sessão): exceção de SQL era engolida e virava lista vazia,
indistinguível de "não existe mapper".** `GetMappersByLayoutGuidForPackagesAsync`
(`MapperDatabaseService.cs:284-346`) tinha `catch (Exception ex) { _logger.LogError(...); return
mappers; }` — QUALQUER exceção (timeout, falha de auth, servidor fora do ar, connection string
vazia) virava `List<Mapper>` vazia. Isso escalava por `GetRankedMapperCandidatesForLayoutGuidAsync`
até `LowCodeAutoTransformationService.TransformAndPersistAsync` (linha 130-134), que trata "0
candidatos" como `Applicable=false` — EXATAMENTE o mesmo resultado de "mapper não existe". O
controller (`TransformationExecutionController.ExecuteSysmiddleCandidatesAsync:303-307`) então
emite sempre a mesma mensagem opaca: "Nenhum mapeador low-code encontrado para o layout... (pathway
sysmiddle)". Ou seja: se a query real estivesse falhando por qualquer motivo de infra em produção
(exatamente a suspeita da Hipótese A), o sintoma observado pelo dono (`candidates: []` + esse
warning) seria IDÊNTICO ao de "não existe mapper" — não dava pra distinguir as duas causas só
olhando a resposta HTTP.

**Fix aplicado:** troquei o `return mappers` do catch por `throw` (relança a exceção original,
mantendo o `LogError` que já registrava ela). Verificado que é seguro: os dois ÚNICOS chamadores
de `LowCodeAutoTransformationService.RunAsync` (`ParseController.cs:215-291` e
`TransformationExecutionController.cs:282-334`) já têm `try/catch` ao redor da chamada, e já
tratam falha estrutural com uma mensagem DIFERENTE de "não encontrado"
(`transformationsStatus="error"` / `"Pathway sysmiddle falhou: {mensagem saneada}"`) — o
comportamento de resiliência (nunca derrubar o parse principal) continua garantido, só que agora
com a distinção certa entre "SQL falhou" e "banco respondeu zero linhas". `GetBestMapperForLayoutGuidAsync`
(o outro chamador de `GetMappersByLayoutGuidForPackagesAsync`) não tem nenhum caller em produção
(só aparece em comentários/memórias de outros agentes) — não precisa de proteção adicional.

Teste de regressão: `Falha_de_banco_ao_buscar_candidatos_propaga_como_excecao_em_vez_de_Applicable_false`
em `tests/LayoutParserApi.Tests/Transformation/LowCodeAutoTransformationCacheTests.cs` (fake
`MapperDbFalho` que lança `InvalidOperationException` no seam virtual
`GetRankedMapperCandidatesForLayoutGuidAsync`; assert `RunAsync` propaga, não devolve
`Applicable=false`). `dotnet build` limpo (só warnings pré-existentes) e suíte completa
295/295 passando.

**Isso não fecha o caso — reduz a superfície.** Se em produção a causa raiz for mesmo uma falha
de SQL (conexão, timeout, permissão), agora ela aparece como `"Pathway sysmiddle falhou: ..."` /
`transformationsStatus="error"` em vez de se disfarçar de "sem mapper" — e o log estruturado
`_logger.LogError(ex, "Erro ao buscar mapeadores por LayoutGuid (filtrado): {LayoutGuid}", ...)`
(nível Error, distinto do `LogWarning` de "0 candidatos") já existia e agora tem efeito real.

**Pergunta objetiva pro dono (próximo passo):** reproduzir a chamada real de
`execute-candidates` pra `LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1` (mesma que deu `candidates:
[]`) e:
1. Ver se a resposta agora vem como `"Pathway sysmiddle falhou: ..."` (confirma que era exceção
   de SQL — aí o próximo passo é achar a causa da exceção: connection string, firewall, timeout)
   ou continua `"Nenhum mapeador low-code encontrado..."` (aí a causa é outra, ainda não
   identificada, e a investigação de código volta à estaca zero — não sobrou mais hipótese óbvia
   pra revisar sem log/trace real de uma chamada que falhe).
2. Se puder, verificar no log estruturado da API (Serilog) se há uma entrada `Error` com a
   mensagem "Erro ao buscar mapeadores por LayoutGuid (filtrado)" no horário da chamada que
   originou o `candidates: []` — isso confirma/descarta a Hipótese A sem precisar reproduzir de
   novo.
