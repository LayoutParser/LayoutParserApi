---
name: artifact-provenance-f2-issue-341-2026-09-09
description: F2 (proveniência real/sintético) do ADR de provedor de LLM plugável — issue #341, branch feat/artifact-provenance-341 a partir de feat/llm-provider-abstraction-340; teste bloqueado por quebra pré-existente não relacionada
metadata:
  type: project
---

Issue #341 (F2 do ADR `docs/architecture/adr-llm-provider-plugavel-2026-09-08.md`) implementada em
`feat/artifact-provenance-341` (commit `d3783a3`), criada a partir de `feat/llm-provider-abstraction-340`
(#340, F1, ainda local/não mergeada em `develop`) — [[llm-provider-abstraction-f1-issue-340-2026-09-09]].

**O que mudou:** `ArtifactProvenance { Synthetic, RealCustomerSample }` novo em
`Models/Entities/Fiscal/PackageArtifact.cs`, com `ResolveSensitivity(string?)` fail-closed (ausência/
inválido → sempre `DataSensitivity.RealFiscalDocument`). Campo `Provenance` propagado ponta a ponta:
entidade → `ArtifactSummary`/`UploadedArtifactInput`/`ArtifactFileRef` → controller (form field opcional
`"{kind}Provenance"`) → `FiscalPackageService` → `SqlFiscalPackageStore` (coluna nova
`tbPackageArtifact.Provenance`, ALTER idempotente separado do CREATE TABLE) → `SqlMappingDraftStore` →
`MappingSuggestionService` (que agora resolve a sensibilidade real por artefato em vez do hardcode
incondicional anterior). `SyntheticDataGeneratorService` só ganhou comentário documentando o ponto de
religamento futuro — nada foi religado, `UseAI` continua 100% ignorado.

**CORREÇÃO (2026-09-09, mesmo dia):** minha hipótese original ("quebra pré-existente não relacionada,
assinatura de `EnqueueAsync` divergente") estava ERRADA e foi refutada por verificação independente do
dono em dois worktrees limpos (`origin/develop` e `feat/llm-provider-abstraction-340` isolados —
ambos compilavam limpo). A causa raiz real: os 6 arquivos de teste (`TransformationExecutionController*
Tests.cs` × 5 + `AiTransformationCandidateServiceXslSynthesizerTests.cs`) referenciavam
`Models.Entities.ParsedField`/`Models.Entities.Mapper`/`Models.Parsing.ParsingResult` **sem o prefixo
`LayoutParserApi.`** — como não havia `using LayoutParserApi.Models.Entities;`, o compilador resolvia
`Models.Entities` relativo ao namespace do arquivo (`LayoutParserApi.Tests`), procurando
`LayoutParserApi.Tests.Models.Entities` (inexistente) → `CS0234`, e por tabela o `SpyAiCandidateService`/
`NoopAiCandidateService` não implementava a interface (`CS0535`) porque o tipo do parâmetro nem
resolvia. **Não era mudança de assinatura de interface por outra branch — era erro de qualificação de
namespace já presente no meu próprio commit `d3783a3`.** Fix: qualificar totalmente com
`LayoutParserApi.Models.Entities.*`/`LayoutParserApi.Models.Parsing.*` (commit `84c8e7c`).
`dotnet test`: 719/719 passando depois do fix.

**Lição:** antes de declarar "pré-existente e não relacionado" no corpo de um commit, rodar
`dotnet build tests/.../*.csproj` isoladamente e ler o erro de perto (CS0234 é forte sinal de
namespace mal qualificado, não de assinatura de interface — CS0535 nesse caso era efeito colateral,
não causa) em vez de assumir a partir do nome do método na mensagem de erro.

**Decisão de design que não estava 100% fechada na issue:** o form field de proveniência no upload
multipart não estava especificado — escolhi `"{kind}Provenance"` (ex.: `"sampleProvenance"`) como
convenção nova, opcional, sem tocar no contrato existente de campos de arquivo por `Kind`.
