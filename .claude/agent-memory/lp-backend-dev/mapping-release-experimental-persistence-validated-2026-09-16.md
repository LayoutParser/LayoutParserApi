---
name: mapping-release-experimental-persistence-validated-2026-09-16
description: Round-trip real de persistência do artefato experimental RAG->XSLT (caso NFe006c_InutNFe) via SqlMappingReleaseStore contra o IdentityDatabase — validado manualmente, teste não ficou no repo.
metadata:
  type: project
---

**Tarefa:** branch `feat/xslt-generation-loop-corpus-neogrid` — a memória de
[[xslt-generation-loop-corpus-neogrid-inut-2026-09-16]] (lp-parser-llm) tinha registrado a
persistência em banco como bloqueio 2 (não implementado, sem credencial na sessão). Nesta sessão
a credencial já estava em `dotnet user-secrets` — implementei e RODEI o round-trip real.

## O que foi validado (rodada real, não simulada)

Escrevi um teste de integração temporário (`tests/LayoutParserApi.Tests/Database/
MappingReleasePersistenceIntegrationTests.cs`, REMOVIDO antes do commit — ver decisão abaixo) que:

1. Lia `IdentityDatabase:UserId`/`Password` via `AddUserSecrets("2aefc4e0-99bb-4143-b95b-5a01bf22d27a")`
   (mesmo `UserSecretsId` da API) + `Server`/`Database` do `appsettings.json` (não é segredo).
2. Montava a cadeia de FK mínima com a API pública dos stores (nenhum método `internal`):
   `SqlFiscalPackageStore.EnsureProjectExistsAsync` → `CreatePackageAsync` →
   `SqlMappingDraftStore.CreateDraftAsync` → `SqlMappingReleaseStore.CreateOrGetCompiledReleaseAsync`.
3. Persistia um artefato `xslt` reconstruído a partir da análise campo-a-campo já documentada na
   memória do experimento (10/11 campos de `<infInut>` — `@Id`, `tpAmb`, `xServ`, `cUF`, `ano`,
   `CNPJ`, `mod`, `serie`, `nNFIni`, `nNFFin`; `xJust` e o wrapper estrutural do documento ficaram
   de fora, como já registrado lá). **O `.xsl` bruto gerado na rodada real não sobreviveu entre
   sessões** (ficou em `.claude/tmp/scratch/run-inut/`, fora do git, dado de sessão) — o conteúdo
   persistido é uma reconstrução marcada como tal no próprio texto do artefato (comentário XML
   inicial), não o candidato bit-a-bit original.
4. Lia de volta por um caminho DIFERENTE do de escrita (`ListByWorkspaceAsync`, não o
   `MappingReleaseDetail` devolvido pelo próprio INSERT) — confirma SELECT real, não só o objeto
   em memória da chamada de escrita.

**Resultado real da rodada** (`dotnet test --filter
FullyQualifiedName~MappingReleasePersistenceIntegrationTests`, passou):
`ReleaseId = 1d612a3c-8b30-4fcb-9fdf-dad1b085608b`, `DraftId = 36251748-c94f-4107-b47b-0129ca542e35`,
`WorkspaceId = 4d8d1cc8-2f5e-402f-bcd9-6155cd48cc13` — conteúdo e hash do artefato bateram
IDÊNTICOS entre a escrita e a releitura via `ListByWorkspaceAsync`. Confirma que
`IMappingReleaseStore`/`SqlMappingReleaseStore` funcionam ponta-a-ponta contra o
`IdentityDatabase` real (172.25.32.5) para este cenário — não só contra mock/host inexistente
(padrão usual dos testes de resiliência desta suíte).

## Decisão: teste NÃO ficou no repo

**Why:** o step `dotnet test` do `ci-dev.yml` (linha ~292) roda SEM as env vars
`IdentityDatabase__UserId`/`Password` (essas só são injetadas mais tarde, no ambiente do serviço
deployado — ver linhas ~658-679 do mesmo workflow) e sem `dotnet user-secrets` configurado no
runner. Um teste que exige essa credencial quebraria o quality gate de QUALQUER PR, não faria
skip gracioso — a única forma de "pular" seria o mesmo `Assert.False(IsNullOrWhiteSpace(...))`
que uso pra falhar alto quando a credencial falta, e falhar alto em CI (onde a credencial nunca
vai existir nesse step) é pior que não rodar. **How to apply:** se algum dia esse padrão de teste
precisar existir de verdade no CI, a pré-condição é o `ci-dev.yml` passar a injetar
`IdentityDatabase__*` no step de `dotnet test` (hoje só no de deploy) — decisão de infra, não de
código, fica com `@lp-devops`.

## Marcação de "experimento" usada — NÃO é o enum `MappingReleaseArtifactSource`

A memória do experimento original já tinha identificado o próximo passo correto (adicionar um
valor `Experimental` ao enum + método dedicado), mas isso muda o contrato de
`IMappingReleaseStore.CreateOrGetCompiledReleaseAsync` (o INSERT hoje hardcoda
`ArtifactSource=DEFAULT 'compiled'` no schema, nenhum parâmetro aceita override) — fora de escopo
desta tarefa, seguindo a mesma decisão de esperar `@lp-architect` alinhar antes de mexer em
contrato de release real. Usei em vez disso `CorrelationId = "EXPERIMENT:
xslt-generation-loop-corpus-neogrid-inut-2026-09-16"` como marcador — mecanismo já existente,
sem mudar schema. **Consequência:** hoje não há um jeito automatizado de listar/filtrar "releases
que são experimento" via `ArtifactSource` — só via grep no `CorrelationId`. Se o volume de
experimentos crescer, vale revisitar a decisão do enum.

## Artefatos desta sessão

- Nada ficou versionado além desta memória — o teste e o `PackageReference` de
  `Microsoft.Extensions.Configuration.UserSecrets` adicionados temporariamente ao
  `tests/LayoutParserApi.Tests/LayoutParserApi.Tests.csproj` foram revertidos antes do commit.
- Build (`dotnet build`, solution inteira) confirmado verde após a reversão.
