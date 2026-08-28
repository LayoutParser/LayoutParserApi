---
name: fieldmappings-execute-candidates-141-doc
description: Onde ficou a implementação real de #141 (worktree separado) e como o README documenta fieldMappings
metadata:
  type: project
---

Ao receber a tarefa de documentar `fieldMappings` em `execute-candidates` (#141), a branch
`feat/fieldmappings-execute-candidates-141` **não estava checked out** na working tree principal
(que estava em `feat/resolucao-estrutural-txt-xml-140`, onde `TransformationCandidate` só tem
`SegmentMappings`, sem `FieldMappings`). A implementação real (commit `ed8f0bb`) já existia num
worktree separado em `/mnt/c/Users/elson.lopes/source/repos/LayoutParserApi-wt-141`.

**Why:** [[concorrencia-git-worktree-isolado]] — padrão desta sessão é isolar em worktree quando
branch/lock instáveis. Antes de assumir "não implementado", verificar `git branch -a` e
`git worktree list` — pode já existir em outro worktree, não é preciso recriar.

**How to apply:** ao documentar qualquer issue, sempre confirmar em qual branch/worktree o código
realmente está antes de ler os arquivos do enunciado da tarefa como fonte de verdade — o enunciado
pode estar referenciando uma branch diferente da working tree ativa no momento.

## O que foi documentado (README.md, seção "7. API & Endpoints")

Nova subseção `fieldMappings` em `execute-candidates`, bilíngue, com:
- Bloco de alerta (`> ⚠️`) logo no topo, antes de qualquer outra explicação, com a ressalva de
  validação comportamental pendente (LowCodeRunner é Windows-only, não roda em WSL/Linux; só há
  validação estrutural sintética de 20 fixtures; dono autorizou seguir mesmo assim).
- Exemplo JSON completo do payload de `execute-candidates` com `fieldMappings` preenchido
  (CNPJ do emitente → `/nfe:NFe/nfe:infNFe/nfe:emit/nfe:CNPJ`).
- Tabela de semântica `null` (tcl-xsl categórico, ou falha isolada de composição)
  vs `[]` (sysmiddle sem resolução) vs preenchido.
- Distinção explícita `fieldMappings` (campo) × `sectionMappings`/`segmentMappings`
  (linha/seção, issue #138) — reforçando que são complementares, não substitutos.

**Gap confirmado nesta sessão:** `sectionMappings`/`segmentMappings` (#138/#126) segue **sem
seção própria no README** — só é citado de passagem no campo `segmentMappings` da tabela de
`pathwayDiagnostics`/diagnóstico. Ver [[sectionmappings-readme-doc-issue138]] para retomar.

## Swagger — XML docs não chegam ao schema

Projeto **não tem `GenerateDocumentationFile`/`IncludeXmlComments` configurado** (nem no
`.csproj` nem em `Program.cs`, só `AddSwaggerGen()` puro). Os XML docs cuidadosamente escritos em
`StructuralResolutionModels.cs`/`TransformationCandidate.cs` são corretos e completos, mas
**não aparecem como descrição no Swagger UI** — só o schema tipado (via reflexão) aparece. Isso é
uma lacuna pré-existente do projeto, não introduzida pela #141 — não foi corrigida nesta sessão
(fora do escopo pedido; envolve mudança de `.csproj`, delegar a `@lp-backend-dev`/`@lp-devops` se
o dono quiser resolver).

## Commit

`ce10166` em `feat/fieldmappings-execute-candidates-141`, no worktree
`/mnt/c/Users/elson.lopes/source/repos/LayoutParserApi-wt-141` — **não** na working tree principal.
Não fiz push (regra do agente).
