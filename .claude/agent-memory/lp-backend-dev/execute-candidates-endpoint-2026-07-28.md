---
name: execute-candidates-endpoint-2026-07-28
description: Implementação do Gap 1 (multi-candidato) — POST /api/transformation-execution/execute-candidates, decisões de design não 100% fechadas no contrato.
metadata:
  type: project
---

Implementado em 2026-07-28: `POST /api/transformation-execution/execute-candidates` em
`Controllers/TransformationExecutionController.cs`, seguindo
`docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md` (Gap 1). Build verde (`dotnet build`,
0 erros). Modelos novos em `Models/Transformation/TransformationCandidate.cs`
(`TransformationCandidate`/`TransformationExecutionCandidatesResponse`). Nenhuma mudança de DI em
`Program.cs` foi necessária — `ILayoutDatabaseService`, `TransformationPipelineService` e
`LowCodeAutoTransformationService` já estavam registrados.

**Decisões de design que o contrato não fechava 100% (e por quê):**

1. **Existência do layout (400) é decidida via consulta ao banco** (`ILayoutDatabaseService.SearchLayoutsAsync`
   com `SearchTerm = LayoutName`, match exato case-insensitive em `Name`). Se a busca no banco lança
   exceção → 500 (mapeei isso para a linha "SQL fora do ar impedindo sequer listar candidatos" da
   tabela — a busca de layout É o que alimenta a listagem de candidatos do pathway sysmiddle). Se a
   busca funciona mas não acha nada → 400 "Layout não encontrado". O pathway tcl-xsl (baseado em
   arquivo, não em banco) roda de qualquer forma depois disso — não fiz uma segunda checagem de
   existência separada pra ele; se o TCL/MAP/XSL não existir em disco, isso vira falha isolada de
   candidato (warning), não 400 global.

2. **Pathway sysmiddle só roda se a entrada for TXT** (`isXmlInput == false`), mesma premissa já usada
   em `ParseController.Upload` (gate por `detectedType != "xml"`). Não há sinalização explícita disso
   no contrato, mas é consistente com `LowCodeAutoTransformationService` esperar texto posicional.

3. **Timeout do CONJUNTO (504)**: contrato diz "reaproveite os timeouts já configurados
   (`LowCode:RunnerTimeoutSeconds`/`LowCode:MaxConcurrentRunners`)" mas não dá a fórmula. Decisão: budget
   = `RunnerTimeoutSeconds * MaxConcurrentRunners` (defaults 15s * 2 = 30s). Raciocínio: candidatos
   sysmiddle competem pelo mesmo semáforo do runner; pior caso plausível é esperar por um slot livre e
   então rodar. Se alguém achar esse budget errado no futuro, é essa fórmula que precisa mudar (não
   documentada em nenhum outro lugar do código).

4. **`CandidateId`**: `sysmiddle-{MapperGuid}` para candidatos low-code, `tclxsl-1` fixo para o pathway
   canônico (ele só produz no máximo 1 candidato — `TransformationPipelineService` não tem noção de
   múltiplos TCL/XSL pro mesmo layout).

5. **`Score`**: nenhum dos dois pathways produz score hoje (`LowCodeCandidateResult` não tem esse campo,
   `TransformationPipelineResult` também não). `RecommendedCandidateId` cai sempre no fallback "primeiro
   candidato da lista" na prática atual — a lógica de "maior Score" está implementada e pronta pra
   quando algum pathway passar a preencher `Score`, mas não há score real ainda.

6. **`Validation`**: só aplicado ao candidato tcl-xsl (via `TransformationValidatorService`, mesmo
   comportamento do endpoint `execute` quando `request.Validate == true`). Sysmiddle não tem validação
   XSD cabeada nesse pathway — já era assim antes (ver comentário em `LowCodeCandidateResult.Success`).

Pontos de atenção pra quem mexer nisso depois: `TransformationPipelineResult.SegmentMappings` é
`Dictionary<int,string>` (chave por índice), mas o contrato pede `Dictionary<string,string>?` — fiz
`.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)` na conversão.

Ver também [[ai-roadmap-2026-07-21-dex-scope]] para contexto do roadmap maior de IA em que este Gap 1
se encaixa.
