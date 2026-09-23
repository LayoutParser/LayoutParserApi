---
name: pr-207-merge-conflict-resolution
description: PR #207 (issue #141) tinha conflitos reais de merge com develop por overlap entre 5 PRs da mesma cadeia; resolvido preservando ambas as features.
metadata:
  type: project
---

Em 2026-08-28, PR #207 (`feat/fieldmappings-execute-candidates-141` → `develop`) ficou
`CONFLICTING` porque `develop` já tinha absorvido as 4 PRs irmãs da mesma cadeia de trabalho
(#200 issue #86, #201 issue #139, #203 issue #138, #205 issue #140) — todas tocando os mesmos
arquivos que #207 (branch cortada antes desses merges, em worktree isolado).

**Achado principal:** `LowCodeCandidateResult.cs` tinha dois campos com nomes diferentes
carregando o **mesmo dado** (`mapper.DecryptedContent`): `DecryptedMapperContent` (issue #141,
usado por `TryComposeFieldMappings`) e `MapperDecryptedContent` (issue #138, usado por
`SysmiddleSectionMappingResolver`). Unificado em um único campo (`DecryptedMapperContent`) —
não eram features conflitantes, era duplicação de nome por desenvolvimento paralelo em
worktrees isolados sem visibilidade um do outro.

`TransformationExecutionController.cs`: `TransformationCandidate` agora popula
`FieldMappings` (#141) E `SectionMappings`/`XmlNamespaces` (#138) no mesmo objeto — são
funcionalidades complementares (granularidade campo vs. linha/seção), ambas preservadas no
mesmo candidato.

`security-code-scan-baseline.json`: reincidência do padrão documentado no próprio `_readme`
do arquivo (linha é chave frágil) — 2 entradas de `LowCodeAutoTransformationService.cs`
reconciliadas de 370/414 (branch da PR) e 366/410 (develop) para 371/415 (linha real
pós-merge). Confirmado lendo o arquivo, mesmos achados de sempre (`File.WriteAllTextAsync`
em `inPath`/`metaPath`), não vulnerabilidade nova. Ver [[pr-198-ci-scs0018-bloqueado]] e
memórias irmãs (#200/#201/#203/#205) para o histórico completo desse padrão de falso positivo.

**Como reproduzi:** worktree temporário isolado (`git worktree add /tmp/...`, fora do checkout
principal), `git merge origin/develop`, resolvi os 5 arquivos, `dotnet build` (0 erros) +
`dotnet test` (413/417 — as 4 falhas são pré-existentes, testes com paths Windows hardcoded
falhando sob WSL/Linux, não relacionadas ao merge), commit, push, removi o worktree.
Resultado: `gh pr view 207` passou de `mergeable: CONFLICTING` para `MERGEABLE`.

**Por que isso importa para o futuro:** cadeias de PRs construídas em worktrees paralelos
(padrão já estabelecido neste projeto — ver `agent-handoff.md`) vão colidir sempre que
tocarem os mesmos arquivos-chave (`TransformationExecutionController.cs`,
`LowCodeAutoTransformationService.cs`, `LowCodeCandidateResult.cs`, `README.md`,
`security-code-scan-baseline.json`). Antes de resolver um conflito às cegas, verificar se os
dois lados são a MESMA coisa com nome diferente (rename duplicado) antes de assumir que é
um clash real que exige escolher um lado.
