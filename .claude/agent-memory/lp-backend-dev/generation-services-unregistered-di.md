---
name: generation-services-unregistered-di
description: Achado (2026-07-21) — quase todo o cluster Services/Generation está sem registro no DI (Program.cs), não só o RAGService do item 1.4. DataGenerationController e o endpoint Gemini do XmlAnalysisController também quebram em runtime.
metadata:
  type: project
---

Investigando o item 1.4 do roadmap de IA (RAGService/RAGController quebrados em
runtime por falta de registro no DI), descobri que o problema é maior do que o
escopo dispatchado: NENHUM serviço de `Services/Generation/Implementations` /
`Services/Generation/Interfaces` está registrado em `Program.cs` — confirmado
por grep (`AddScoped<T>`/`AddSingleton<T>` para cada tipo, zero resultado
antes da minha correção). Isso inclui:

- `GeminiAIService` (consumido por `XmlAnalysisController.AnalyzeXsdErrorWithAi`)
  e suas próprias dependências `ILayoutTypeDetector`, `ILineValidator`,
  `IFieldGenerator` (nenhuma registrada).
- `ISyntheticDataGeneratorService`, `IExcelDataProcessor`, `ILayoutAnalysisService`,
  `TxtFileGeneratorFactory` — todos consumidos por `DataGenerationController`
  (nenhum registrado).

Corrigi **só** `RAGService` (exatamente o item 1.4), deliberadamente:
- `GeminiAIService` está no meio da decisão de decommission Gemini/OpenAI
  (itens 1.2/1.3, ainda não fechada quando escrevi isto) — registrá-lo agora
  seria comprometer-se a mantê-lo vivo antes da hora.
- O gap do `DataGenerationController` nunca foi dispatchado por ninguém (a
  Aria só flagou o `RAGController` no roadmap) — fora do escopo pedido nesta
  rodada, reportei em vez de consertar sem autorização.

**Why:** evita eu (ou outra sessão futura) assumir "RAGController era o único
controller quebrado, já resolvido" — não é. `DataGenerationController` inteiro
e o endpoint `XmlAnalysisController.AnalyzeXsdErrorWithAi` continuam
derrubando em runtime hoje (nunca chegaram a subir, então provavelmente
ninguém percebeu ainda em uso real).

**How to apply:** se o usuário relatar `/api/DataGeneration/*` ou
`analyze-xsd-error-with-ai` falhando com erro de resolução de DI, a causa é
esta (não um bug novo). Quando os itens 1.2/1.3 forem destravados, lembrar que
o registro de DI do cluster inteiro de Generation precisa ser resolvido, não
só o `GeminiAIService` em si — senão o decommission "resolve" o Gemini mas
deixa `DataGenerationController` quebrado do mesmo jeito.
