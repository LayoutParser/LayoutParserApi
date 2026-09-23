---
name: generate-sample-endpoint-issue-355-2026-09-09
description: Endpoint POST /api/layouts/{layoutGuid}/generate-sample (issue #355) — reuso de XmlLayoutLoader/SyntheticDataGeneratorService, GetBestMapperForLayoutGuidAsync virou virtual para testabilidade
metadata:
  type: project
---

Branch `feat/endpoint-generate-sample-355` (a partir de `develop`, commit `8219b40`, não
pushada). Novo `Controllers/LayoutsController.cs` — não existia controller "api/layouts" antes.

**Peças reaproveitadas sem atrito, vale lembrar onde estão:**
- `ICachedLayoutService.GetLayoutByGuidAsync` já devolve `LayoutRecord.DecryptedContent`
  **já decifrado** (não precisa chamar `IDecryptionService` de novo no controller).
- `Services/Generation/Implementations/XmlLayoutLoader.LoadLayoutFromXmlString(xml)` é o
  parser certo de `LayoutVO` XML → `Models.Entities.Layout` (usado por
  `DataGenerationController` também) — evita reinventar o parse XML manual que
  `LayoutValidationService.ParseLayoutFromXml` faz em paralelo (duplicação pré-existente no
  repo, não resolvida aqui, fora de escopo).
- `MapperDatabaseService.GetBestMapperForLayoutGuidAsync(layoutGuid, projectId,
  allowedPackageGuids)` é EXATAMENTE o método certo pra pré-condição "tem mapper" — já existia,
  só não era `virtual`. Adicionei `virtual` (mesmo padrão de
  `GetRankedMapperCandidatesForLayoutGuidAsync`, comentário idêntico) pra poder testar sem SQL.
- `AddGenerationServices()` (`Services/Generation/GenerationServiceCollectionExtensions.cs`)
  JÁ registra `ISyntheticDataGeneratorService` no DI — a landmine antiga de
  [[generation-services-unregistered-di]] (2026-07-21) foi corrigida em algum commit posterior
  não rastreado por mim nesta sessão; **não é mais um problema real hoje**, confirmei lendo o
  arquivo e o `Program.cs:693`. Atualizar a memória antiga seria bom, mas não fiz — está listada
  como achado histórico, não bug atual.

**Decisão de design não coberta pelo ADR, resolvida por mim:** `seed` no contrato não tem
suporte real no `SyntheticDataGeneratorService` (usa `Random` de instância, sem overload com
seed). Em vez de ignorar silenciosamente ou inventar suporte fora de escopo, retornei um warning
explícito no response quando `seed.HasValue` — mantém o contrato (não quebra quem manda o campo)
sem fingir que funciona.

**Why:** documentar porque a pré-condição de mapper e o reuso de `XmlLayoutLoader`/
`SyntheticDataGeneratorService` são o desenho certo pra qualquer extensão futura (ex.: issue
#356, cobertura de layout `Xml`) — reaproveitar os mesmos pontos, não duplicar geradores de
valor (CPF/CNPJ/data) num serviço novo, conforme a Decisão 3 do ADR.

**How to apply:** se a #356 (layout Xml) for pega depois, o parser de árvore novo deve
compartilhar os geradores de valor com `SyntheticDataGeneratorService` (extrair um
`IFieldValueGenerator` comum), não copiar `GenerateCnpj`/`GenerateCpf`/etc. de novo.
