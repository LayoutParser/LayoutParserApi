---
name: reference-examples-catalog-neogrid-2026-09-16
description: Decisão de expor o corpus TCL/XSL da Neogrid como catálogo separado (não ArtifactSource em MappingRelease) — GET /api/reference-examples
metadata:
  type: project
---

Endpoint novo `GET /api/reference-examples` (+ `GET /api/reference-examples/{id}` para conteúdo)
expõe o corpus real de exemplos TCL/XSL da Neogrid (referência/oráculo, não releases compilados
pelo pipeline) sem tocar no domínio `MappingDraft`/`MappingRelease`.

**Decisão (Protocolo IDS):** endpoint separado, não um novo `ArtifactSource` em `MappingRelease`.
Razão: esses exemplos não têm `workspaceId` nem passaram pela governança de release real — forçá-los
no domínio de `MappingRelease` exigiria um workspace "sistema"/demo fake e um `ArtifactSource` que
não representa nem `compiled` nem `manual_edit`, poluindo o modelo real de governança. Um catálogo
plano, sem escopo de workspace, é mais fiel ao que o dado realmente é (arquivo estático de
referência).

**Implementação:**
- `Services/Fiscal/IReferenceExampleCatalogService.cs` + `ReferenceExampleCatalogService.cs` — lê
  `{BasePath}/tcl/{DocType}/{Versao}/*.tcl` e `{BasePath}/xsl/{DocType}/{Versao}/*.xsl`, pareando
  por nome-base (case-insensitive). Reconstrói o catálogo do disco a cada chamada (corpus pequeno,
  somente-leitura — não justifica cache/invalidação).
- Config `ReferenceExamples:BasePath` em `appsettings.json`, vazio por padrão — **path local da
  sessão de trabalho (`.claude/temp/servidor/layoutparser/Examples/`) não foi hardcoded**; quem for
  ativar o catálogo em produção precisa apontar o BasePath pro local real do corpus no host.
- `Models/Fiscal/ReferenceExample.cs` — DTO de metadados (sem conteúdo) + `ReferenceExampleContent`
  (conteúdo sob demanda, via `GET /{id}`), evitando payload pesado na listagem.
- Registrado em `Program.cs` no grupo Fiscal (perto de `IRequiredCoverageCalculator`).

**Testes:** `tests/LayoutParserApi.Tests/Services/Fiscal/ReferenceExampleCatalogServiceTests.cs` —
fixture sintética em diretório temp (não o corpus real), cobrindo par completo, tcl sem par
correspondente, filtro por `docType`, leitura de conteúdo por id, e os dois casos de degradação
graciosa (BasePath vazio / diretório inexistente).

**Nota de sessão concorrente:** durante esta tarefa, outra sessão trocou a branch corrente na
mesma working tree (mesmo padrão de [[sessoes-concorrentes-commit-por-item]] — ver
`.claude/agent-memory/lp-backend-dev/MEMORY.md`). `git add` foi feito por caminho explícito nos 5
arquivos novos + `Program.cs`/`appsettings.json` (confirmado via `git diff` que só continham minha
mudança antes de commitar) — nenhum arquivo da outra sessão (`Controllers/ParseController.cs`,
`Services/Learning/LayoutLearningService.cs`, testes de `ParseController*`) foi tocado ou commitado.
