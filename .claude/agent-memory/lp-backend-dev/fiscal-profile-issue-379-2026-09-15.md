---
name: fiscal-profile-issue-379
description: Implementação completa do FiscalProfile (draft/release) issue #379 — onde ficou cada peça e a concorrência real que rolou durante a sessão
metadata:
  type: project
---

Issue #379 (ADR `docs/architecture/adr-perfil-fiscal-draft-release-2026-09-10.md`) implementada
inteira em `feat/fiscal-profile-379` (commit `e9dd82d`, local, não pushado): modelo
`FiscalProfile`/`FiscalDocumentType`/`FiscalOperation`/`FiscalJurisdiction`/`FiscalResolvedXsd`
em `Models/Entities/Fiscal/FiscalProfile.cs`; coluna `FiscalProfileJson` em `tbMappingDraft` e
`tbMappingRelease` via `EnsureSchemaAsync` idempotente; `IFiscalProfileResolver`/
`FiscalProfileResolver` (`Services/Fiscal/`) faz a cascata de validação do §2.4 e normaliza a
chave `CTE`(appsettings)→`CTe`(enum) num dicionário interno — **não** mexi no `appsettings.json`;
endpoint `PUT /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/fiscal-profile` com
`[RequireWorkspaceRole(Mapper, FiscalAdmin, Owner)]`; `MappingCompileService` copia
`draft.FiscalProfile` pra release e adiciona diagnóstico `warning` (não erro) quando ausente;
GETs de draft e de release expõem `fiscalProfile`+`resolvedXsd` (resolvedXsd é sempre derivado
em runtime via `IFiscalProfileResolver.Resolve`, nunca persistido).

**Why:** pré-requisito da #380 (cobertura de obrigatórios do XSD) — sem perfil não dá pra saber
qual XSD é o alvo do mapeamento.

**Como aplicar:** ao trabalhar na #380, o ponto de entrada é `release.FiscalProfile` (snapshot
imutável) + `IFiscalProfileResolver.Resolve(documentType, schemaVersion)` pra achar o XSD.
`MappingDraft.FiscalProfile` é só a cópia de trabalho — nunca usar pra cálculo de cobertura
(regra do ADR §2.2).

## Concorrência real durante a implementação

Rodou em paralelo (mesma working tree, sem isolamento de branch/worktree real) pelo menos:
uma **sessão duplicada minha mesma** trabalhando na #379 (achei stubs de `SetFiscalProfileAsync`
já prontos em arquivos que eu nunca tinha editado — `MappingExplanationAdaptersTests.cs`,
`MappingTestRunServiceTests.cs` — e um `SetFiscalProfileAsync` duplicado por `CS0111` num arquivo
que EU tinha acabado de editar); a #381 (edição manual de artefato — `ArtifactSyntaxValidator.cs`,
`MappingArtifactEditControllerTests.cs`, campos `ArtifactSource`/`DerivedFromReleaseId`/etc. em
`MappingReleaseDetail`); e a #367 (diff granular por regra — `MappingTestRunDivergenceGroup` em
`MappingRelease.cs`). Builds falharam e se recuperaram sozinhos 2-3 vezes só esperando (30-60s)
o outro agente terminar o hunk que estava quebrado no meio da gravação — **não tentei "consertar"
nada fora do meu escopo**, só re-tentei o build depois de uma pausa. Ver
[[sessoes-concorrentes-commit-por-item]] — apliquei a mesma disciplina: `git add` só dos arquivos
que eu de fato autorei para a #379, mesmo que o `git status` mostrasse outros arquivos
modificados por essas sessões paralelas (deixei-os de fora do commit).

`MappingReleaseDetail`/`MappingDraftDetail` (records posicionais) ganharam o campo `FiscalProfile`
como **último parâmetro com default** especificamente para não quebrar os ~15 call sites
existentes (produção + testes) — padrão que vale a pena repetir quando outro slice precisar
adicionar campo a um desses records: sempre trailing + default, nunca inserir no meio.
