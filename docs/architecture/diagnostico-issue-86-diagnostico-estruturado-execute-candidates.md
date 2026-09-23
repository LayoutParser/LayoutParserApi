# Diagnóstico — LayoutParserReact #86: `candidates: []` para LAY_CNHI_..._ENVNFE_4.00_NFe

> Autoria: `@lp-architect` (Aria). Missão `review-arch`, cross-repo (issue LayoutParserReact #86,
> criada 2026-08-12T23:04). Não implementa nada — desenho + plano de divisão de trabalho.

## 0. Veredito em uma frase

A issue #86 é **o mesmo sintoma já investigado a fundo em 2026-08-12** (mesmo dia, mesmo layout —
`LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe` / `LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1`) e **descreve
um estado do código anterior a dois fixes já mesclados**. As duas mensagens citadas na issue
("nenhum mapeador low-code" / "arquivo MAP não encontrado") batem exatamente com os warnings que o
código *ainda* produz hoje quando os pathways não acham nada — mas o comportamento que vem *depois*
dessas mensagens mudou: desde `c65157d` (2026-08-16) a API não mais devolve só `candidates: []` +
2 warnings soltos; ela também enfileira o **fallback automático de IA** e devolve um 3º warning com
o ticket de acompanhamento. O que a issue pede — "produzir candidato válido ou explicar de forma
definitiva e comprovada por que cada pathway não se aplica" — já está parcialmente resolvido pelo
mecanismo existente, mas **não da forma que o pedido descreve** (um contrato estruturado
`pathwayDiagnostics[]`, não warnings de texto livre). O gap real remanescente é esse contrato, não
uma causa raiz nova a caçar.

## 1. Linha do tempo (evita reabrir capítulos já fechados)

| Data | Evento |
|---|---|
| 2026-08-12 | Issue #86 aberta. Mesmo dia, `@lp-backend-dev` roda 4 capítulos de investigação (`.claude/agent-memory/lp-backend-dev/execute-candidates-ausencia-total-para-cnhi-envnfe.md`): SQL confirma mapper existe e é elegível; acha e corrige (a) exceção de SQL engolida virando `Applicable=false` silencioso, (b) convenção errada de caminho do `.tcl` no pathway tcl-xsl. |
| 2026-08-16 | `34a4833`/`c65157d` implementam o pathway IA em `execute-candidates` (issue #40) — antes disso a IA **nunca era chamada** por este endpoint, é a 3ª causa do capítulo 1 da memória de 08-12. |
| 2026-08-20 | `@lp-architect` reconfirma (`docs/architecture/diagnostico-candidates-vazio-cnhi-2026-08-20.md`) que as 3 causas de 08-12 seguem corrigidas no código, a pedido do front. |
| 2026-08-27 (hoje) | Releitura de código confirma: os 3 fixes continuam presentes; `TryEnqueueAiFallback` está corretamente cabeado e é chamado quando `candidates.Count == 0` (`TransformationExecutionController.cs:287-288`). |

## 2. Evidência de código, ponto a ponto

### 2.1 `layoutGuid`/`layoutName` — resolução (confirma correto, não é a causa)

- O controller resolve o layout **por nome** (`SearchLayoutsAsync(request.LayoutName)`), não confia
  no `LayoutGuid` do payload. `LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid,
  layoutRecord.LayoutGuid)` é chamado tanto no pathway sysmiddle (`TransformationExecutionController.cs:329`)
  quanto no fallback de IA (linha 456) — mesmo resolver, mesmo fallback pro GUID do catálogo se o
  do request for inválido. 4 testes de regressão cobrem isso
  (`tests/LayoutParserApi.Tests/Transformation/LowCodeLayoutGuidResolverTests.cs`).

### 2.2 Catálogo/SQL — mapper existe (confirmado por dado real em 2026-08-12, não é a causa)

`SELECT ProjectId, PackageGuid, MapperGuid, Name FROM tbMapper WHERE InputLayoutGuid =
'LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1'` devolveu 2 linhas reais, `ProjectId=2` (bate com
`LowCode:ProjectId`), `PackageGuid` dentro de `LowCode:AllowedPackageGuids`. Catálogo correto.

### 2.3 Pathway sysmiddle — mensagem da issue nasce em `TransformationExecutionController.cs:349-355`

```csharp
if (!autoResult.Applicable)
{
    warnings.Add($"Nenhum mapeador low-code encontrado para o layout {request.LayoutName} (pathway sysmiddle)");
    failureKinds.Add(FailureKind.NotApplicable);
    return result;
}
```

`autoResult.Applicable` vem de `GetMappersByLayoutGuidForPackagesAsync`
(`Services/Database/MapperDatabaseService.cs:284`). Antes do fix de 08-12, uma exceção de SQL
qualquer virava silenciosamente `return mappers` (lista vazia) — hoje relança (`throw`, linha ~343),
o que faz esse caminho virar `"Pathway sysmiddle falhou: ..."` (`FailureKind.ExecutionInfraError`,
linha 380-387) em vez de `"Nenhum mapeador encontrado"`. **Se a issue #86 for reproduzida hoje e a
mensagem ainda vier como "Nenhum mapeador low-code encontrado", o SQL respondeu 0 linhas de fato —
não é mais o bug do catch engolido.** Combinado com o SQL do item 2.2 (mapper existe), isso é
inconsistente e precisa de reprodução real com log/CorrelationId (ver §5) — não há mais hipótese de
código a revisar sem esse dado.

### 2.4 Pathway tcl-xsl — mensagem da issue nasce em `TransformationPipelineService.cs:151-155`

```csharp
var mapContent = await LoadMappingFileAsync(layoutName);
if (mapContent == null)
    result.Errors.Add($"Arquivo MAP não encontrado para layout: {layoutName}");
```

`LoadMappingFileAsync` (linha ~382) hoje monta `Path.Combine(_tclBasePath,
"{layoutName}.tcl")` — fix de 08-12 (capítulo 4), confirmado como o padrão real de produção
(`tcl/LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.tcl` existe no dump). Se a issue #86 for reproduzida
hoje e ainda vier "Arquivo MAP não encontrado", ou (a) `TransformationPipeline:TclPath` não está
configurado/aponta para pasta errada no ambiente onde rodou, ou (b) o arquivo realmente não existe
nesse ambiente específico (diferente do dump usado em 08-12). Não é mais o bug de convenção de nome.

**Gap não corrigido, achado em 08-12 e ainda presente:** `FindXslFile`
(`TransformationPipelineService.cs`, usado nas linhas 109 e 309) não bate com a convenção real de
nome do XSL (`{mapperName}_{layoutName}.xsl`) e cai no fallback "primeiro XSL da pasta" — mas isso
só importa **depois** que o `.tcl` já resolveu, não é o que está fazendo `candidates: []` para este
layout hoje.

### 2.5 Fallback de IA — cabeado corretamente, mas silencioso se suprimido

`TryEnqueueAiFallback` (linhas 443-495) só é chamado se `candidates.Count == 0` (linha 287-288) e só
dispara a IA se **nenhum** `failureKinds` for `ExecutionInfraError` (linha 449-454). Dado o estado
de §2.3/§2.4 (ambos os pathways reportando `NotApplicable`, não `ExecutionInfraError`), o fallback
**deveria** disparar e adicionar o warning `"Nenhum candidato de transformação encontrado —
fallback automático de IA enfileirado (ticket ...)"`. Três motivos possíveis para esse warning não
aparecer na reprodução da issue:

1. **`IAiFallbackSuppressionGate.IsInCooldown`** (linha 463) — se uma tentativa anterior já falhou
   para este `LayoutGuid`, o fallback é suprimido silenciosamente por um período (mensagem de
   cooldown, não de "sem IA"). Precisa checar o estado do gate em memória no processo do ambiente
   onde a issue foi reproduzida (reinicia a cada restart do processo).
2. **`resolvedLayoutGuidText` não é um GUID válido** (linha 457) — improvável dado §2.1, mas
   tecnicamente possível se `layoutRecord.LayoutGuid` do catálogo estiver malformado.
3. **A reprodução que gerou a issue foi feita ANTES de `c65157d` (2026-08-16)** — a issue foi
   aberta em 2026-08-12, 4 dias antes do fallback existir. Esta é a explicação mais provável e
   consistente com a timeline: a issue descreve um estado do sistema que já não existe mais.

## 3. Causa raiz — não é uma linha de código, é um contrato de resposta insuficiente

Não há evidência de bug de código novo. A causa raiz do incômodo relatado na issue é de **design de
contrato**, não de lógica: mesmo com o fallback de IA funcionando, a resposta síncrona ainda é
`candidates: []` + array de `warnings: string[]` de texto livre — o front não tem como distinguir
programaticamente "não modelado, IA cuidando" de "infra quebrada, IA não vai ajudar" de "config
errada neste ambiente". É exatamente o gap que o pedido do usuário identifica ao propor
`pathwayDiagnostics[]`.

## 4. Desenho do contrato de diagnóstico estruturado

### 4.1 Contrato aditivo (não quebra consumidores existentes)

```json
{
  "success": true,
  "candidates": [],
  "recommendedCandidateId": null,
  "warnings": ["... mensagens atuais, preservadas por compatibilidade ..."],
  "pathwayDiagnostics": [
    {
      "pathway": "sysmiddle",
      "status": "not_applicable",
      "code": "no_mapper",
      "message": "Nenhum mapeador low-code encontrado para o layout LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe"
    },
    {
      "pathway": "tcl-xsl",
      "status": "failed",
      "code": "map_not_found",
      "message": "Arquivo MAP não encontrado para o layout"
    },
    {
      "pathway": "ai-fallback",
      "status": "candidate_generated",
      "code": "not_applicable",
      "message": "Fallback automático de IA enfileirado (ticket 3f9a...); consulte GET execute-candidates/{ticket}/ia-status"
    }
  ],
  "correlationId": "1e2b3c4d-..."
}
```

`Warnings` continua existindo e populado exatamente como hoje — **aditivo**, não substitutivo,
conforme pedido. `pathwayDiagnostics` é a versão estruturada e estável em `code`, pensada para
consumo por máquina (front pode renderizar badge/ícone por `status`/`code` sem fazer parsing de
string).

### 4.2 Taxonomia de `status` (enum estável, 3 valores)

| `status` | Significado |
|---|---|
| `candidate_generated` | Pathway produziu ao menos 1 candidato (inclui o ticket assíncrono do fallback de IA, que "gera" no sentido de estar em processamento). |
| `not_applicable` | Pathway não achou nada aplicável a este layout — não é erro, é ausência de cobertura (equivale a `FailureKind.NotApplicable`). |
| `failed` | Pathway tentou e quebrou por causa de infra/config (equivale a `FailureKind.ExecutionInfraError`). |

### 4.3 Taxonomia de `code` (mínimo pedido + extensões óbvias do código já existente)

| `code` | Pathway(s) | Quando |
|---|---|---|
| `no_mapper` | sysmiddle | `autoResult.Applicable == false` (linha 349-355) |
| `map_not_found` | tcl-xsl | `LoadMappingFileAsync` devolve `null` (`TransformationPipelineService.cs:151-155`) |
| `xsl_not_found` | tcl-xsl | `FindXslFile` devolve vazio/arquivo inexistente (linhas 109-113, 309-313) — hoje indistinguível de `map_not_found` na mensagem, precisa separar |
| `configuration_error` | ambos | Exceção capturada mas identificável como config ausente (ex.: `TclPath`/`AllowedPackageGuids` vazio) — hoje cai genericamente em `execution_error` |
| `runner_unavailable` | sysmiddle | Candidato individual falha com mensagem do runner (linha 369-377, hoje vira `ExecutionInfraError` genérico) |
| `execution_error` | ambos | Exceção estrutural não classificada mais especificamente (fallback genérico, linhas 380-388 e 584-590) |
| `timeout` | sysmiddle | Já existe sinal de timeout no nível do endpoint (linha 263-269, HTTP 504) — não chega a virar item de `pathwayDiagnostics` hoje porque a resposta inteira aborta antes. Decisão de design: manter 504 para timeout do conjunto (não misturar com diagnóstico por pathway) ou expor por pathway se o timeout for parcial (só um dos dois pathways estourou). Recomendo manter 504 como está — não introduzir `timeout` em `pathwayDiagnostics` nesta rodada, evita ambiguidade entre "resposta parcial" e "resposta completa com timeout reportado". |
| `not_applicable` | ai-fallback | Entrada fora de escopo do pathway (ex.: XML de entrada para sysmiddle, linha 324-325) — reaproveita o mesmo `code` do sysmiddle/tcl-xsl por consistência semântica, mas pathway = `"ai-fallback"` ou o próprio pathway de origem, a definir na implementação (ver §6, item Dex). |

`code` é string, não enum C# exposto — permite adicionar valores novos sem quebrar contrato
(convenção já usada em outros lugares deste repo para superfícies versionadas por string).

### 4.4 Fonte de cada `pathwayDiagnostics[i]` no código atual

Não é um subsistema novo — é **materializar** a informação que `failureKinds` (`ConcurrentBag<FailureKind>`)
e as mensagens de `warnings` já carregam, mas hoje descartada na fronteira do contrato de resposta.
`ExecuteSysmiddleCandidatesAsync`/`ExecuteTclXslCandidatesAsync` já retornam exatamente os 2
sinais necessários (mensagem + `FailureKind`) — só precisam devolver também o `code` (hoje
implícito só no comentário, precisa virar dado) em vez de só empilhar em `warnings`/`failureKinds`
soltos.

## 5. Regra de sanitização (achado incidental — gap real, distinto da issue)

O pathway sysmiddle já sanitiza mensagens antes do wire: `LowCodeErrorSanitizer.ForWire(ex)`
(`TransformationExecutionController.cs:385`), que troca caminhos absolutos por
`[caminho interno]`. **O pathway tcl-xsl não usa o mesmo sanitizador** — `warnings.Add($"Pathway
tcl-xsl falhou: {ex.Message}")` (linha 587) e `warnings.Add($"Candidato tcl-xsl falhou: {string.Join("; ",
pipelineResult.Errors)}")` (linha 548) vazam `ex.Message`/`Errors` crus. Como
`TransformationPipelineService` já usa `ex.Message` em vários `Errors.Add` internos (ex.: linha 172,
326, 366 — mensagens de `IOException`/`XmlException` que podem conter caminho de arquivo), isso é
uma inconsistência de sanitização pré-existente, não introduzida por este diagnóstico, mas que
**deve** ser corrigida junto do novo contrato — o campo `message` de `pathwayDiagnostics` é
exatamente o lugar onde esse vazamento reapareceria se não for coberto.

**Regra para a implementação:** todo `message` que compõe `pathwayDiagnostics[].message` ou
`warnings[]` passa por `LowCodeErrorSanitizer.ForWire(...)` antes de sair no payload HTTP — sem
exceção, nos dois pathways. Detalhes crus (caminho físico completo, connection string, stack trace)
só podem existir no log estruturado (`_logger.LogWarning/LogError`), nunca no corpo da resposta.
`CorrelationId` no payload é seguro (não é segredo) e permite ao suporte cruzar com o log completo
sanitizado.

## 6. Plano de implementação por dono

| Dono | Escopo |
|---|---|
| **`@lp-backend-dev` (Dex)** | (a) Adicionar `PathwayDiagnostic` a `Models/Transformation/TransformationCandidate.cs` (ou novo arquivo `Models/Transformation/PathwayDiagnostic.cs`) com `Pathway`/`Status`/`Code`/`Message` (strings, não enum exposto para `Status`/`Code` — ver §4.3). (b) Adicionar `List<PathwayDiagnostic> PathwayDiagnostics` a `TransformationExecutionCandidatesResponse`. (c) Adicionar `string CorrelationId` ao mesmo response, lido do `LogContext`/`HttpContext` já existente (`Program.cs:676-697` tem o mecanismo, só precisa expor). (d) Aplicar `LowCodeErrorSanitizer.ForWire(...)` nas 3 mensagens não sanitizadas do pathway tcl-xsl (linhas 548, 571, 587 de `TransformationExecutionController.cs`) — fix independente, pode sair antes do contrato novo. |
| **`@lp-parser-llm` (Lia)** | (a) Em `ExecuteSysmiddleCandidatesAsync`/`ExecuteTclXslCandidatesAsync`/`TryEnqueueAiFallback`, além de popular `warnings`/`failureKinds` como hoje, popular também um `PathwayDiagnostic` com o `code` certo por ramo (mapa §4.4). (b) Separar `map_not_found` de `xsl_not_found` em `TransformationPipelineService` — hoje ambos caem em mensagens parecidas de "arquivo não encontrado"; precisa que `LoadMappingFileAsync` e `FindXslFile` sinalizem a causa de forma distinguível (não só string) para o controller montar o `code` certo. (c) Investigar/instrumentar por que o fallback de IA pode não ter disparado na reprodução da issue (§2.5) — checar `IAiFallbackSuppressionGate` em runtime real, não só ler o código. |
| **`@lp-devops` (Gage)** | Nenhuma diferença de config identificada como causa nesta rodada — mas se a reprodução real (§7) mostrar `TclPath`/`AllowedPackageGuids` ausente/errado no ambiente de teste, é ação dele corrigir `appsettings`/env var, não código. |
| **`@lp-qa` (Quinn)** | Validar o gate: `pathwayDiagnostics` presente e coerente com `warnings` (mesma contagem de causas, nenhuma mensagem com caminho absoluto cru) + teste de regressão do plano §7. |
| **`@lp-doc` (Duda)** | Atualizar Swagger/XML doc do endpoint `execute-candidates` com o novo campo aditivo, e o README/CHANGELOG do contrato se houver um. |

## 7. Plano de reprodução automatizada (documento sintético, não real)

Reaproveitar o padrão já usado em `TransformationPipelineServiceMapFileTests.cs`
(fixture minimal com a mesma estrutura raiz do `.tcl` real) e
`LowCodeAutoTransformationCacheTests.cs` (fake que lança exceção no seam virtual). Não usar TXT
real nem conteúdo de documento de cliente — só estrutura sintética suficiente para acionar cada
ramo:

1. **Caso `no_mapper` (sysmiddle):** fake de `IMapperDatabaseService`/`GetRankedMapperCandidatesForLayoutGuidAsync`
   devolvendo lista vazia (não exceção) para um `LayoutGuid` sintético qualquer (ex.:
   `LAY_00000000-0000-0000-0000-000000000001`) — assert `pathwayDiagnostics` contém item
   `pathway=sysmiddle, status=not_applicable, code=no_mapper`.
2. **Caso `map_not_found` (tcl-xsl):** `TclPath` de teste apontando para diretório temporário vazio
   (`Path.GetTempPath()` + subpasta descartável, sem nenhum `.tcl` dentro) e `layoutName` sintético
   — assert `pathway=tcl-xsl, status=failed, code=map_not_found`. Reaproveita o padrão de fixture já
   existente em `TransformationPipelineServiceMapFileTests.cs`.
3. **Caso `execution_error` (sysmiddle infra):** mesmo fake do item 1, mas lançando
   `InvalidOperationException` em vez de devolver lista vazia (já existe: `MapperDbFalho` em
   `LowCodeAutoTransformationCacheTests.cs`) — assert `status=failed, code=execution_error` (ou
   `runner_unavailable` se a mensagem simulada for do runner) e que o fallback de IA **não** foi
   chamado (mock de `IAiTransformationCandidateService.EnqueueAsync` com `Times.Never`).
4. **Caso sanitização:** exceção sintética com `Message` contendo um caminho absoluto Windows
   fabricado (`@"C:\fake\path\nao-existe.tcl"`) forçada em ambos os pathways — assert que nenhuma
   string em `warnings`/`pathwayDiagnostics[].message` contém `C:\` nem `\\`.
5. **Caso fallback de IA disparado:** combinar 1+2 (nenhum candidato, nenhum `ExecutionInfraError`)
   com mock de `IAiFallbackSuppressionGate.IsInCooldown` retornando `false` — assert
   `pathwayDiagnostics` contém item com `status=candidate_generated` (ou `not_applicable`, a
   decidir na implementação) e `code` referenciando o ticket, e que `EnqueueAsync` foi chamado
   uma vez.

Nenhum desses casos precisa de acesso a SQL real, ao filesystem de produção, nem a
`LayoutParserDecrypt.exe` — todos operam com fakes/mocks dos serviços de domínio, seguindo o
padrão de teste já estabelecido no projeto (`tests/LayoutParserApi.Tests/Transformation/`).
