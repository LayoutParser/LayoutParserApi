---
name: xmlanalysiscontroller-di-fix-2026-07-29
description: XmlAnalysisController inteiro quebrava em runtime por GeminiAIService não registrado no DI — corrigido removendo a dependência do construtor e o endpoint morto que a usava.
metadata:
  type: project
---

Confirmado e corrigido (2026-07-29): `Controllers/XmlAnalysisController.cs` exigia
`GeminiAIService` no construtor (nunca registrado no DI — Gemini decomissionado,
ver [[generation-services-unregistered-di]]). Isso derrubava **todos** os
endpoints do controller na ativação, não só o que usava IA:
`analyze`, `validate-file`, `validate-xsd`, `transform-nfe`, `orientations`
também quebravam, mesmo sem relação com Gemini.

**O que foi feito:**
- Removida a dependência `GeminiAIService _geminiAIService` do construtor.
- Removido o endpoint `POST analyze-xsd-error-with-ai` (único consumidor real
  de `_geminiAIService`, confirmado por leitura completa do arquivo — usava
  `BuildAiPrompt` + `CallGeminiAPI`) e seus dois métodos privados auxiliares
  (`BuildAiPrompt`, `MapXmlErrorToMqSeriesSegment`), que só existiam para
  suportar esse endpoint.
- Removida a classe `XsdErrorAnalysisRequest` (só usada por esse endpoint;
  confirmado via grep no repo inteiro antes de deletar).
- Mantido o `using LayoutParserApi.Services.Generation.Implementations;`
  porque `XmlLayoutLoader` (usado em `analyze` e `validate-file`) mora no
  mesmo namespace que `GeminiAIService` — build quebrou na primeira tentativa
  por remover o using inteiro, corrigido devolvendo o using.
- Não reimplementei o caso de uso com Ollama aqui: o substituto funcional já
  existe em `Controllers/ValidationDiagnosticController.cs`
  (`POST /api/xml-analysis/diagnose-validation-error`, mesma rota base
  `api/xml-analysis`), que inclusive já documenta em XML doc por que é um
  controller separado. Confirmei por leitura que cobre o mesmo caso de uso
  (diagnóstico de erro de validação via LLM).

**Why:** o construtor exigindo um serviço nunca registrado no DI é o padrão
de bug já visto no cluster Generation inteiro — a lição aqui é que a falha
de DI é *por controller*, não por endpoint: um único endpoint dependente de
serviço não registrado derruba o controller inteiro na ativação.

**How to apply:** `dotnet build` limpo (0 erros) após a mudança. Se o usuário
relatar `analyze`/`validate-file`/`validate-xsd`/`transform-nfe`/`orientations`
falhando com erro de resolução de DI, isso já foi corrigido nesta sessão —
verificar se a regressão não é uma reintrodução do `GeminiAIService` no
construtor. Se aparecer relato de outro controller inteiro quebrado por um
único serviço de Generation não registrado, o padrão de correção é o mesmo:
não basta não chamar o método, tem que tirar do construtor.
