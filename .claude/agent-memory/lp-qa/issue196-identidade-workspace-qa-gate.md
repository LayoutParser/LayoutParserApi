---
name: issue196-identidade-workspace-qa-gate
description: LayoutParserReact#196 — idempotência concorrente de identidade PROVADA contra IdentityDatabase real; isolamento cross-workspace com 1 brecha (CreateDraft/packageId)
metadata:
  type: project
---

Branch `test/identidade-workspace-196` (cb0fec1, local, sem push). Idempotência: store SQL trata corrida
(UNIQUE + catch 2601/2627 + releitura); 8 rodadas x 30 paralelas no IdentityDatabase real = 1 user/1 workspace,
153 corridas tratadas, 0 falhas; dados de teste (provider `qa-test-196`) apagados. Técnica: projeto scratch com
ProjectReference à API + `AddUserSecrets(<UserSecretsId>)` + appsettings.json (Server/Database), instanciando
`SqlIdentityWorkspaceStore` direto (contorna a trava em processo = pior caso multi-instância).

Isolamento: teste por reflexão `Security/WorkspaceIsolationReflectionTests.cs`; 22 ações sem filtro estão numa lista de
exceções POR AÇÃO (isolam via membership manual/JOIN em store). Achado: `MappingDraftsController.CreateDraft`
(`RevisionBelongsToPackageAsync` não checa workspace do pacote) aceita packageId/revisionId de outro workspace.

**How to apply:** ao revisar novo endpoint com `{workspaceId}`, o teste de reflexão falha até haver filtro ou exceção justificada.
Ambiente: em worktree, comandos compostos com `cd` fora do worktree são recusados — usar Write para arquivos scratch.
