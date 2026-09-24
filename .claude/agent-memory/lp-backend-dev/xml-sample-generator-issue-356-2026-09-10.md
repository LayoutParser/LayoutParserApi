---
name: xml-sample-generator-issue-356
description: Issue #356 — geração de documento de exemplo para layout tipo Xml no endpoint generate-sample; spike, decisão de nome e colisão de IFieldValueGenerator
metadata:
  type: project
---

Issue #356 (branch `feat/xml-layout-sample-generator-356`, commit `9337385`): o endpoint
`POST /api/layouts/{guid}/generate-sample` (base #355, [[generate-sample-endpoint-issue-355-2026-09-09]])
passou a cobrir layout tipo `Xml`, não só `TextPositional`.

**Resultado do spike (critério de aceite 1):** nenhum parser de runtime C# reconstrói a árvore do
`LayoutVO` Xml para reserializá-la. `XmlLayoutLoader` e
`Services/Generation/TxtGenerator/Parsers/XmlLayoutParser.cs` só entendem `LineElementVO`/`FieldElementVO`.
O único que anda `GroupTag/Tag/Attribute` é `XslSynth.Core.GuidXPathCatalog` (assembly separado
`ai/XslSynth.Contracts`), mas achata para índice GUID→XPath — sem reter Sequence/ocorrência nem
serializar. Daí serviço novo (`IXmlSampleDocumentGeneratorService`), reusando só as convenções de
caminhada: `AttributeElementVO` vira atributo do pai; `ChoiceElementVO`/`SequenceElementVO` são
wrappers estruturais (não viram elemento).

**Colisão de nome:** já existe `Services/Generation/TxtGenerator/Generators/Interfaces/IFieldValueGenerator.cs`
(acoplado a `FieldDefinition` + `recordIndex`, impl `Deterministic/RandomGenerator`). Por isso o
componente compartilhado pedido no aceite 3 virou **`ITypedValueGenerator`/`TypedValueGenerator`**
(`Services/Generation/Implementations`), não `IFieldValueGenerator`. Ele concentra os geradores por
tipo (CPF/CNPJ/data/decimal — DV módulo 11 da #357) e é consumido pelos dois caminhos;
`SyntheticDataGeneratorService` foi refatorado para delegar (ctor agora recebe `ITypedValueGenerator`).

**Why:** evitar duplicar `GenerateCnpj/GenerateCpf/GenerateDate` e manter um único ponto de verdade
dos dígitos verificadores.

**How to apply:** ambos registrados em `AddGenerationServices` (`GenerationServiceCollectionExtensions`),
não solto no `Program.cs`. Ao mexer no ctor de `SyntheticDataGeneratorService` ou `LayoutsController`,
lembrar que há 3 testes que os instanciam à mão
(`LayoutsControllerGenerateSampleTests`, `SyntheticDataGeneratorServiceCpfCnpjTests`,
`XmlSampleDocumentGeneratorServiceTests`). `dotnet test` cheio: 751 verdes (1ª execução teve
"test host crashed" no teardown — flake, 2ª execução limpa).
