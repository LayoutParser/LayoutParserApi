# Contrato de API — Multi-candidato de transformação e Diagnóstico de erro via IA

> Origem: pedido do front-end (`LayoutParserReact`, branch `feat/document-analysis-tab`, commit `9ec563a`).
> Status: gaps confirmados por `@lp-architect` (Aria) — nenhum dos dois contratos existe hoje, ambos são trabalho novo.
> Este documento fecha as decisões de design que faltavam (tratamento de erro/timeout/casos-limite) antes de dispachar a implementação.

## Gap 1 — Multi-candidato de transformação

### Situação atual

`POST /api/transformation-execution/execute` retorna resultado singular. O multi-candidato
(`LowCodeAutoTransformResult`/`LowCodeCandidateResult`) só existe no fluxo de upload TXT
(`ParseController.Upload`), não no pathway de execução direta.

### Endpoint novo

`POST /api/transformation-execution/execute-candidates`

**Request:** idêntico ao `TransformationRequest` já usado por `execute` (`InputContent`, `LayoutName`,
`SourceDocumentType?`, `TargetDocumentType?`, `Validate?`, `ExpectedOutput?`) — sem campo novo.

**Response sucesso (200):**
```csharp
public class TransformationCandidate
{
    public string CandidateId { get; set; }
    public string Pathway { get; set; }          // "sysmiddle" | "tcl-xsl"
    public string TransformedXml { get; set; }
    public double? Score { get; set; }
    public Dictionary<string, string>? SegmentMappings { get; set; }
    public object? Validation { get; set; }
    public string? FailureReason { get; set; }    // preenchido só quando o candidato falhou parcialmente (ver tabela)
}

public class TransformationExecutionCandidatesResponse
{
    public bool Success { get; set; }
    public List<TransformationCandidate> Candidates { get; set; } = new();
    public string? RecommendedCandidateId { get; set; }
    public List<string> Warnings { get; set; } = new();
}
```

### Tabela de decisão — casos-limite

| Caso | HTTP | `success` | `candidates` | Observação |
|---|---|---|---|---|
| **N candidatos, todos OK** | 200 | `true` | array com N itens | `recommendedCandidateId` = maior `Score`, ou primeiro se não houver score |
| **1 candidato apenas** | 200 | `true` | array com 1 item | **Mesmo shape** — não existe um "response singular" separado. O front decide não mostrar seletor quando `candidates.length === 1`; isso é decisão de UI, não de contrato. |
| **Zero candidatos configurados pro layout** (layout existe, mas nenhum mapper associado) | 200 | `true` | `[]` | Não é erro — é estado de dado válido ("este layout ainda não tem mapeador"). `Warnings` inclui `"Nenhum candidato de transformação encontrado para o layout {LayoutName}"`. Front trata array vazio como "sem candidato disponível", não como falha de rede. |
| **Layout não existe / não encontrado** | 400 | `false` | — | Erro de requisição (`LayoutName` inválido) — mesmo padrão de erro do `execute` já usa (`{ success:false, errors, warnings }`). |
| **`InputContent`/`LayoutName` ausentes** | 400 | `false` | — | Validação de request, igual ao `execute` atual. |
| **Falha parcial** (alguns candidatos completam, outros falham/timeoutam individualmente) | 200 | `true` | só os que completaram | **Não é erro geral** — segue o mesmo princípio de isolamento por candidato já implementado em `LowCodeAutoTransformationService` (`Task.WhenAll` com isolamento de exceção por candidato). Candidatos que falharam **não aparecem no array** (nunca retornar item com `TransformedXml` nulo); a falha vira uma entrada em `Warnings` (ex.: `"Candidato {CandidateId} (pathway sysmiddle) falhou: timeout do runner"`). |
| **Falha total de infraestrutura** (SQL/Redis fora do ar impedindo sequer listar candidatos, ou todos os candidatos falham por causa comum de infra) | 500 | `false` | — | Erro genuíno de servidor — distingue de "zero candidatos configurados" porque aqui a causa é infraestrutura, não ausência de dado. |
| **Timeout do processo como um todo** (ex.: SemaphoreSlim satura, fila de espera excede limite) | 504 | `false` | — | Usa o mesmo timeout já configurado (`LowCode:RunnerTimeoutSeconds`/`LowCode:MaxConcurrentRunners`) — se o conjunto inteiro de candidatos não conseguir nem começar a rodar dentro do budget, retorna 504 em vez de deixar o cliente pendurado. |

**Princípio geral:** erro HTTP (400/500/504) é reservado pra quando a requisição em si não pôde ser processada. Ausência de resultado (zero candidatos, candidato individual que falhou) é dado válido dentro de uma resposta 200 — o front não deveria tratar "não achei candidato" como falha de sistema.

---

## Gap 2 — Diagnóstico de erro via IA (Ollama)

### Situação atual

`XmlAnalysisController.AnalyzeXsdErrorWithAi` depende de `GeminiAIService` — decomissionado por
decisão de arquitetura ([[gemini-openai-decommission-decision]]), sem registro confirmado no DI.
Não existe hoje um caminho via Ollama para este diagnóstico.

### Endpoint novo

`POST /api/xml-analysis/diagnose-validation-error`

**Request:**
```csharp
public class ValidationDiagnosticRequest
{
    public string ErrorMessage { get; set; } = "";
    public string? FieldName { get; set; }
    public string? MqSeriesSegment { get; set; }
    public string? DocumentType { get; set; }
    public string? TransformedXml { get; set; }
}
```

**Response sucesso (200):**
```csharp
public class ValidationDiagnostic
{
    public string Summary { get; set; } = "";
    public string? SuggestedFix { get; set; }
    public double? Confidence { get; set; }   // 0.0–1.0
}

public class ValidationDiagnosticResponse
{
    public bool Success { get; set; }
    public ValidationDiagnostic? Diagnostic { get; set; }
    public string? Error { get; set; }
}
```

### Tabela de decisão — casos-limite

| Caso | HTTP | `success` | Observação |
|---|---|---|---|
| **Diagnóstico gerado, alta ou média confiança** | 200 | `true` | `diagnostic.confidence` preenchido; front decide o limiar visual (ex.: >0.7 verde, 0.4-0.7 amarelo) |
| **Diagnóstico de baixa confiança** | 200 | `true` | **Não é erro** — o modelo respondeu, só que com incerteza. `confidence` baixo (ex. <0.4), `summary` presente mas com linguagem de ressalva (ex.: "não foi possível determinar com certeza..."), `suggestedFix` pode vir `null` se o modelo não tiver segurança suficiente pra sugerir correção. Front usa o campo `confidence` pra decidir como apresentar — nunca vira erro HTTP. |
| **`errorMessage` vazio/ausente** | 400 | `false` | Validação de request — mesmo padrão do controller atual (`IsNullOrWhiteSpace`). |
| **Contexto insuficiente mas `errorMessage` presente** (sem `fieldName`/`transformedXml`/etc.) | 200 | `true` | **Não bloqueia** — tenta diagnosticar com o que tem, retornando `confidence` mais baixo por causa da falta de contexto. Rejeitar com 400 aqui seria excessivo; a falta de contexto já se reflete no campo `confidence`. |
| **Provedor (Ollama) indisponível** (endpoint não responde, connection refused) | 503 | `false` | `error`: `"Provedor de IA indisponível no momento"`. Segue o princípio de resiliência do projeto — Ollama é dependência externa que pode cair, response degrada sem derrubar o request principal do usuário (o chamador do endpoint recebe erro claro, não exceção não tratada). |
| **Timeout do modelo local** (Ollama responde mas excede o tempo configurado) | 504 | `false` | `error`: `"Diagnóstico excedeu o tempo limite"`. Precisa de config nova (ex. `Ollama:DiagnosisTimeoutSeconds`), análogo ao padrão já usado em `LowCode:RunnerTimeoutSeconds`. |
| **Erro de infraestrutura genérico** (exceção não tratada) | 500 | `false` | Log com `ILogger`, nunca vazar stacktrace pro client. |

**Princípio geral:** distinguir "o modelo respondeu, mas com pouca certeza" (200, dado com confiança baixa) de "não consegui nem chamar o modelo" (503/504 — falha de infraestrutura). Baixa confiança é informação útil pro usuário; indisponibilidade/timeout é falha de sistema que precisa aparecer como tal.

---

## Próximos passos (dispatch)

- **Gap 1** → `@lp-backend-dev` (Dex): novo endpoint em `TransformationExecutionController`, reaproveitando `LowCodeAutoTransformationService`/pipeline canônico já existentes para produzir os candidatos dos dois pathways (sysmiddle + tcl-xsl) em paralelo.
- **Gap 2** → `@lp-parser-llm` (Lia): novo endpoint em `XmlAnalysisController` (ou controller novo dedicado), usando Ollama (`deepseek-coder:6.7b`, config já existente) no lugar do `GeminiAIService` decomissionado — inclui timeout/disponibilidade conforme tabela acima.
