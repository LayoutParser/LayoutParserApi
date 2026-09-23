---
name: xsdvalidator-exige-xsdpath-nao-vazio-em-testes
description: XsdValidator.Validate lança ArgumentNullException se xsdPath for vazio/nulo — testes unitários do RepairOrchestrator precisam de um XSD real em disco, não string.Empty
metadata:
  type: project
---

`ai/XslSynth.Core/Core/XsdValidator.cs` chama `XmlSchemaSet.Add(targetNamespace: null,
schemaUri: xsdPath)` sem checar `xsdPath` antes — `schemaUri` vazio ou nulo lança
`ArgumentNullException` (não devolve um `XsdResult` com erro, como seria o padrão de
degradação graciosa do resto do projeto). Isso não é bug introduzido por mim, é
comportamento pré-existente.

**Why:** ao escrever testes de unidade para `RepairOrchestrator.RunAsync` (F1/F2 do ADR
`adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md`, issue #337), usar
`xsdPath: string.Empty` (achando que "sem XSD" seria um caminho válido de degradação)
quebrou os 3 testes com `ArgumentNullException` antes mesmo de chegar no `CanonicalDiffer`.

**How to apply:** qualquer teste que chame `RepairOrchestrator.RunAsync`/`XsdValidator.Validate`
diretamente precisa gravar um XSD mínimo permissivo em disco (`Path.GetTempPath()` +
`xs:any processContents="skip"` para aceitar qualquer filho) e passar o caminho real — não
`string.Empty`. Ver `ai/XslSynth.Core.Tests/RepairOrchestratorSeedReuseTests.cs` para o padrão
(`WritePermissiveXsd`). Não tentei corrigir o `XsdValidator` em si (fora do escopo da tarefa
F1/F2) — se algum dia isso incomodar em produção (`ResolveXsdPath` já pode devolver `null` →
`?? string.Empty` em `RepairOrchestratorXslSynthesizerService`), vale um `if
(string.IsNullOrWhiteSpace(xsdPath)) return new XsdResult(false, ["XSD não resolvido"]);` no
início do método.
