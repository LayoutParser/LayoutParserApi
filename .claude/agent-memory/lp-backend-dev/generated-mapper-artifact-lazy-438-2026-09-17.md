---
name: generated-mapper-artifact-lazy-438-2026-09-17
description: Endpoint GET .../generated-transformation (issue #438) — geração lazy de TCL/XSL/XSLT reaproveitando o loop determinístico de ai/XslSynth.Core in-process; validationBasis sempre "declared_dsl".
metadata:
  type: project
---

**Tarefa:** implementar o escopo mínimo do ADR `docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md`
(§5) — branch `feat/geracao-automatica-lazy-mapper-438` a partir de `develop` (já com #434/#435/#437/#433 mergeados).

## Contrato final

`GET api/workspaces/{workspaceId:guid}/mappings/{mappingId}/generated-transformation` (mesma
convenção de rota/RBAC de `LayoutTreeController` — divergiu do path sugerido na tarefa
`api/workspaces/.../mappers/{mapperGuid}/generated-artifact` porque o precedente real do repo já
usa `mappings/{mappingId}` para endpoints de leitura por mapper). Resposta:
`{ mapperGuid, status, content?, coverageJson?, validationBasis?, generatedAt?, correlationId? }`.
`status` nunca é `"none"` — se não existia candidato, a própria chamada já dispara a geração e
devolve `"generating"`. `validationBasis` é sempre `"declared_dsl"` quando `ready` (nunca
`"live_execution"` — bloqueio de licença FiatMQ documentado no ADR §2 segue de pé).

## Descoberta que mudou o escopo real: item de "maior esforço" do ADR já estava pronto

O ADR §5 item 2 dizia "adaptar `ai/XslSynth` para rodar como serviço in-process da API... é o
item de maior esforço real deste MVP". Isso já tinha sido feito — `ai/XslSynth.Core` é uma
classlib .NET puro (extraída em decisão anterior, ver `docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md`),
referenciada pela API (`LayoutParserApi.csproj`) e já consumida in-process por
`Services/Transformation/Ai/RepairOrchestratorXslSynthesizerService.cs`. Não precisei portar CLI
nenhum — só reusei as peças determinísticas (`LinkMappingTranspiler`, `DslBlockInterpreter`,
`DslRuleTranslator`, `CandidateBuilder`, `CoverageValidator`, `ProvenancePublisher`), na mesma
sequência do `ai/XslSynth/Program.cs` (passos 1-4 + publicação A6), sem o loop de reparo por
gabarito (`RepairOrchestrator`) — esse exige `groundTruthXml`, que não existe neste caso de uso
(geração sem referência, só cobertura contra o DSL declarado).

## Divergências do ADR (documentadas, não omissão)

1. **GuidXPathCatalog não resolvido** (`TargetCatalog = null` no `LinkMappingTranspiler`) — os
   LinkMappings ficam com destino só até a folha (símbolo), mesmo limite honesto que o CLI
   standalone já documenta. Resolver exigiria a mesma resolução de layout de destino que
   `LayoutTreeService` já faz (via `ICachedLayoutService`); fora do escopo mínimo — a geração
   ainda funciona, só sem o caminho completo do XPath de destino.
2. **Persistência: tabela nova, não `IMappingReleaseStore`.** O ADR não resolveu explicitamente
   a pergunta pendente de sessões anteriores (`Experimental`/`GeneratedCandidate` em
   `MappingReleaseArtifactSource`) — e `IMappingReleaseStore` exige `workspaceId`/`draftId`/
   `rulesSnapshotHash` (cadeia de FK de governança real), que não existe para "gerar a partir só
   do `mapperGuid`". Criei `IGeneratedMapperArtifactStore`/`SqlGeneratedMapperArtifactStore`
   (tabela nova `dbo.tbGeneratedMapperArtifact`, autossuficiente, sem FK — mesmo padrão de
   `SqlFieldCorrectionStore`), chaveada só por `MapperGuid`. Registrado no `FiscalSchemaInitializer`.
3. **Ollama é best-effort, não obrigatório.** Tentei reaproveitar o cliente (`OllamaClient` de
   `ai/XslSynth.Core`) com o mesmo padrão de reachability-check do CLI; se indisponível, cai no
   fallback determinístico (`DslBlockInterpreter` + fallback 1-saída de `DslRuleTranslator`) —
   candidato ainda sai, só sem tradução via LLM das regras não reconhecidas pelo interpreter.

## Concorrência (issue #438 item 5)

`TryBeginGeneratingAsync` é atômico via SQL: tenta `INSERT` primeiro (PK em `MapperGuid` garante
atomicidade); se colidir (erro 2627/2601), faz `UPDATE ... WHERE Status <> 'generating'` — só uma
chamada concorrente "ganha" e dispara `Task.Run`. Sem lock em memória (funciona entre processos,
não só threads do mesmo processo).

## Staleness (ADR §4, sem o job periódico — fora deste MVP)

Hash SHA256 sobre `LinkMappings`/`Rules` NORMALIZADOS (não o XML criptografado bruto) —
`GeneratedMapperArtifactService.ComputeMapperVoHash` (público, não `internal`, para os testes
poderem recalcular sem precisar de `InternalsVisibleTo`). Recalculado a cada `GET` (não há job
periódico varrendo `tbMapper`) — se `status=ready` e o hash divergir do gravado, o próximo `GET`
trata como `stale` e redispara. Latência de detecção = "próxima vez que alguém consultar", não
instantânea — mesma limitação já aceita no ADR §6.

## Teste

`tests/LayoutParserApi.Tests/Services/Transformation/Ai/GeneratedMapperArtifactServiceTests.cs` —
5 testes, sem banco real (`IGeneratedMapperArtifactStore` fake em memória com `TaskCompletionSource`
pra sincronizar com o fire-and-forget). Cobre os 4 estados do contrato + mapper inexistente (404).
Achado durante o teste: `RealMapperParser.Parse` NÃO lança para o formato "sample" (MVP) do
`MapperExtractor` — só produz um `MapperVo` vazio/diferente, então o hash real calculado pelo
serviço é o do `RealMapperParser`, não o do `MapperExtractor` (mesma ambiguidade que já existe em
`RepairOrchestratorXslSynthesizerService.ParseMapperVo`, não é bug novo). O teste replicou a
sequência exata do serviço para calcular o hash esperado.

## Build/testes

`dotnet build` (solution inteira): 0 erros. `dotnet test --filter GeneratedMapperArtifactServiceTests`:
5/5 passaram (~4s, sem I/O de rede real — Ollama aponta pra `127.0.0.1:1`, falha rápido).

## Arquivos

- `Services/Interfaces/IGeneratedMapperArtifactStore.cs` (novo)
- `Services/Database/SqlGeneratedMapperArtifactStore.cs` (novo)
- `Services/Transformation/Ai/IGeneratedMapperArtifactService.cs` (novo)
- `Services/Transformation/Ai/GeneratedMapperArtifactService.cs` (novo)
- `Controllers/GeneratedMapperArtifactController.cs` (novo)
- `Services/Database/FiscalSchemaInitializer.cs` (+1 chamada EnsureSchemaAsync)
- `Program.cs` (+2 registros DI, grupo Fiscal)
- `tests/LayoutParserApi.Tests/Services/Transformation/Ai/GeneratedMapperArtifactServiceTests.cs` (novo)

## Pendências pra outros agentes

- `@lp-doc`: endpoint novo, contrato não documentado no Swagger/README ainda.
- `@lp-qa`: quality gate formal do endpoint (além do teste unitário que já escrevi).
- `@lp-architect`: se o volume de candidatos gerados crescer, revisitar se `tbGeneratedMapperArtifact`
  devia mesmo ficar fora do domínio `MappingRelease` a longo prazo (mesma tensão já registrada em
  [[mapping-release-experimental-persistence-validated-2026-09-16]]).
