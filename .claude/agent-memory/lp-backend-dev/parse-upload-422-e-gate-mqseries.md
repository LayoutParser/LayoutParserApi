---
name: parse-upload-422-e-gate-mqseries
description: O gate detectedType=="mqseries" em ParseController.Upload está fechado DE PROPÓSITO; e o 422 de falha de parse é o primeiro do repo, com o front ainda não ajustado.
metadata:
  type: project
---

Dois pontos sobre `Controllers/ParseController.cs` (endpoint `POST /api/Parse/upload`):

**1. O gate `detectedType == "mqseries"` que restringe o pathway de transformação low-code
está fechado deliberadamente — não é bug, não "conserte".**

**Why:** abrir o gate para outros tipos (idoc/txt) antes de a correção de offsets/split de
linha ficar pronta produz XML de transformação com **dado fiscal corrompido**. Foi
explicitamente marcado como "Fase 3, bloqueada" no diagnóstico da Aria (2026-08-03), com
trabalho paralelo da Lia (@lp-parser-llm) em `Services/Parsing/Implementations/LineSplitter.cs`.

**How to apply:** ao mexer nesse trecho, confirme antes com @lp-parser-llm/@lp-architect se o
bloqueio caiu. Um "if" que parece restritivo demais nesse arquivo merece pergunta, não patch.

**2. O 422 (`UnprocessableEntity`) introduzido em 2026-08-03 no fix `7f54e28` é o primeiro
uso de 422 do repositório inteiro — não existe model de erro compartilhado, é anonymous
object no estilo dos demais `return` do controller.**

**Why:** antes disso, falha de parse virava `500 "Erro interno: Object reference not set..."`,
apagando a causa real. O shape é `{ success=false, detectedType, message }`.

**How to apply:** se for preciso um segundo endpoint com semântica de "entrada não
processável", siga esse mesmo shape em vez de inventar outro — e lembre que o consumidor é o
repo **LayoutParserReact** (`src/components/upload/UploadSection.tsx`), que trata só 2xx e
`catch`. Mudança de contrato aqui exige acompanhamento em outro repositório, então o `git log`
deste repo sozinho não conta a história completa. Ver [[sessoes-concorrentes-commit-por-item]].
