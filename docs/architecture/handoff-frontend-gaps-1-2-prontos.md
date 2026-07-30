# Handoff para @lp-front-dev (LayoutParserReact) — Gaps 1 e 2 prontos no backend

> Repo alvo: `LayoutParserReact`, branch `feat/document-analysis-tab` (base conhecida `9ec563a`).
> Origem: `LayoutParserApi`, branch `develop`. Escrito por `@lp-architect` (Aria) a pedido do usuário —
> handoff de comunicação, sem código anexo. Cole esta mensagem inteira para o `@lp-front-dev`.

## Contexto

Os dois gaps identificados na sessão anterior (multi-candidato de transformação + diagnóstico de
erro de validação via IA) foram **implementados e validados por build** no backend. Este documento
descreve o **contrato real** — já pode integrar.

---

## Gap 1 — Multi-candidato de transformação

**Endpoint:** `POST /api/transformation-execution/execute-candidates`
**Implementado por:** `@lp-backend-dev` (Dex)

### Request
Idêntico ao `execute` que o front já consome:

```json
{
  "inputContent": "string",
  "layoutName": "string",
  "sourceDocumentType": "string | null",
  "targetDocumentType": "string | null",
  "validate": true,
  "expectedOutput": "string | null"
}
```

### Response

```json
{
  "success": true,
  "candidates": [
    {
      "candidateId": "sysmiddle-{MapperGuid}",
      "pathway": "sysmiddle",
      "transformedXml": "string",
      "score": null,
      "segmentMappings": {},
      "validation": null,
      "failureReason": null
    },
    {
      "candidateId": "tclxsl-1",
      "pathway": "tcl-xsl",
      "transformedXml": "string",
      "score": null,
      "segmentMappings": {},
      "validation": { "...": "..." },
      "failureReason": null
    }
  ],
  "recommendedCandidateId": null,
  "warnings": ["string"]
}
```

### Pontos de atenção para a UI

- `candidateId` tem formato previsível: `sysmiddle-{MapperGuid}` para o pathway Sysmiddle;
  `tclxsl-1` **fixo** para o pathway canônico TCL/XSL (esse pathway hoje só produz 1 candidato,
  não itere um número dinâmico para ele).
- **Nenhum pathway preenche `score` de verdade ainda.** Não implemente ordenação por `score` —
  trate como ausente e caia sempre no fallback de "primeiro item do array" até um próximo aviso.
- **`validation` só vem preenchido no candidato `tcl-xsl`.** O pathway `sysmiddle` ainda não tem
  validação XSD cabeada — não trate `validation: null` nele como erro.
- **Zero candidatos é sucesso, não erro:** resposta 200 com `candidates: []` + `warnings` explicando
  o motivo. Trate isso como estado vazio da UI, não como falha de rede/HTTP.
- Um candidato que falha parcialmente **simplesmente não aparece** no array — nunca vem um item com
  `transformedXml: null`. O motivo vai em `warnings` (texto livre), não em um campo por candidato
  além de `failureReason` (que é por candidato quando o candidato existe mas com problema reportável).

---

## Gap 2 — Diagnóstico de erro de validação via IA

**Endpoint:** `POST /api/xml-analysis/diagnose-validation-error`
**Implementado por:** `@lp-parser-llm` (Lia) — via **Ollama local**, não mais Gemini.

### Request

```json
{
  "errorMessage": "string (obrigatório)",
  "fieldName": "string | null",
  "mqSeriesSegment": "string | null",
  "documentType": "string | null",
  "transformedXml": "string | null"
}
```

### Response — sucesso

```json
{
  "success": true,
  "diagnostic": {
    "summary": "string",
    "suggestedFix": "string | null",
    "confidence": 0.0
  }
}
```

`confidence` vai de 0.0 a 1.0. **Confiança baixa nunca vira erro HTTP** — é só um número baixo
nesse campo; trate na UI como "diagnóstico com baixa certeza", não como falha.

### Erros possíveis

| Status | Causa |
|--------|-------|
| 400 | `errorMessage` vazio |
| 503 | Ollama indisponível |
| 504 | Timeout do modelo |
| 500 | Erro de infraestrutura genérico |

### ⚠️ Ressalva importante de performance

O caminho feliz (200 com diagnóstico completo) foi validado **isoladamente contra o Ollama real**,
mas **não foi confirmado end-to-end através do endpoint completo neste ambiente de dev** — a
chamada estourou consistentemente ~150s de latência porque a máquina de dev é **CPU-only, sem
GPU**. Os casos de erro (400/503/504) foram validados normalmente.

**Recomendação para a UI:** trate este endpoint como potencialmente **lento** (dezenas de segundos
a minutos neste ambiente, até haver hardware com GPU em produção) — não como uma chamada rápida.
Use timeout de UI generoso e um estado de loading claro (não um spinner de 2-3s).

---

## Bônus — bug corrigido que pode desbloquear integração já contornada

`XmlAnalysisController` (endpoints `analyze`, `validate-file`, `validate-xsd`, `transform-nfe`,
`orientations`) estava **totalmente quebrado em runtime** (falha de DI por dependência do Gemini
nunca registrada no `Program.cs`) até esta sessão. Está **corrigido agora** — esses 5 endpoints
funcionam normalmente.

O endpoint `analyze-xsd-error-with-ai` que existia nesse controller foi **removido** (era
duplicado do Gap 2 acima, que é o substituto correto via Ollama). **Se o front já tinha alguma
integração apontando para `analyze-xsd-error-with-ai`, precisa migrar para
`diagnose-validation-error`** (contrato do Gap 2 descrito acima).

---

## Resumo para ação imediata

1. Integrar `POST /api/transformation-execution/execute-candidates` — UI de seleção de candidato,
   sem depender de `score`, tratando `validation` como opcional por pathway.
2. Integrar `POST /api/xml-analysis/diagnose-validation-error` — com timeout de UI generoso e
   loading state adequado a uma chamada potencialmente longa.
3. Se existir chamada a `analyze-xsd-error-with-ai`, migrar para o endpoint do item 2.
4. Os 5 endpoints de `XmlAnalysisController` voltaram a funcionar — reavaliar qualquer workaround
   que o front tenha feito por causa da quebra anterior.
