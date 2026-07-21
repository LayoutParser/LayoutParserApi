---
name: gemini-cloud-xsd-diagnosis-gap
description: Todo o subsistema Gemini/OpenAI (GeminiAIService, AIService, RAGService, SyntheticDataGeneratorService, SemanticAIGenerator) não está registrado no DI — XmlAnalysisController, DataGenerationController e RAGController quebram em runtime hoje. Escopo de remoção mapeado (Tier 1/2) + dívida de documentação.
metadata:
  type: project
---

Achado original (2026-07-21, turno 1): `XmlAnalysisController.AnalyzeXsdErrorWithAi` manda erro XSD + até 3000
chars de documento original + até 2000 chars de XML transformado pro Gemini (nuvem), contra a regra de
segurança do projeto (LLM local pra dado sensível).

**Achado ampliado (2026-07-21, turno 2 — muda a gravidade e a urgência):** investigando a decisão de
abandonar Gemini/OpenAI, mapeei TODOS os consumidores desse subsistema — são 4, não 1:

- `XmlAnalysisController.AnalyzeXsdErrorWithAi` → `GeminiAIService.CallGeminiAPI` (diagnóstico de erro XSD).
- `SyntheticDataGeneratorService.GenerateWithGeminiAsync` → `GeminiAIService.GenerateSyntheticData` — **este é
  o mais grave**: `LoadExamplesFromDirectory` lê arquivos `.txt`/`.mq_series` REAIS do disco
  (`Examples:Path`) e o prompt construído em `BuildPromptWithContext` os rotula explicitamente como
  "EXEMPLOS REAIS COMPLETOS (COPIE ESTES PADRÕES)" — manda até 3 arquivos reais, até 15 linhas cada,
  verbatim pro Gemini. Não é só erro XSD isolado; é geração de dados sintéticos usando documento real como
  few-shot.
- `SemanticAIGenerator.GenerateAsync` → `GeminiAIService.CallGeminiAPI` (geração semântica de valor de campo).
- `AIService` (não `GeminiAIService`) → **OpenAI** `gpt-3.5-turbo` hardcoded (`AIService.cs:36`, nem vem de
  config), usado por `AnalyzeFieldPatternsAsync`/`SuggestFieldMappingsAsync`/`GenerateFieldValueAsync` —
  `sampleValues`/`ExcelDataContext` podem conter dado real extraído de documento/planilha do cliente.

**MAS — achado crítico que muda o cálculo de risco:** nenhum desses serviços está registrado no DI.
Confirmado por grep exaustivo (4 padrões diferentes, case-insensitive) em `Program.cs` inteiro (393 linhas,
lido por completo): zero menções a `GeminiAIService`, `AIService`/`IAIService`, `RAGService`,
`SyntheticDataGeneratorService`, `SemanticAIGenerator`, `IFieldGenerator`, `ILayoutTypeDetector`,
`ILineValidator` — nem `AddScoped`/`AddSingleton`/`AddHttpClient`. Isso significa que **`XmlAnalysisController`
E `DataGenerationController` (que depende de `ISyntheticDataGeneratorService`) não conseguem ser
instanciados pelo container de DI hoje** — toda chamada a qualquer endpoint desses dois controllers lança
`InvalidOperationException` em runtime (não é erro de `dotnet build`, que passa normal — é falha de resolução
de dependência no primeiro request). Ou seja: **hoje, na prática, nada está vazando dado real pra nuvem por
este caminho — não porque foi corrigido de propósito, mas porque está quebrado.** Isso reduz a urgência de
exposição ativa, mas não elimina o risco: se alguém "consertar" o registro de DI no futuro sem revisitar essa
decisão, reativa o vazamento sem querer.

`RAGService.cs` (confirmei lendo o arquivo): zero menções a Ollama/Gemini/OpenAI/HttpClient — é retrieval
puro, agnóstico de provedor. Não precisa mudar quando o provedor de geração for trocado.

**Decisão tomada em 2026-07-21 (Aria, passo 0 #2):** abandonar Gemini e OpenAI por completo, não só
"deprioritizar". Ollama assume 100% do papel de LLM nesta API daqui pra frente pro diagnóstico XSD; geração
sintética é caso à parte (fork open-source, não API). Ver [[gemini-openai-decommission-decision]] para o
racional completo, a resposta sobre fine-tuning e a correção de hardware (CPU-only).

## Escopo de remoção (mapeado em 2026-07-21, pro Dex executar — Aria só recomenda, não remove)

Busca exaustiva de quem referencia essas classes em todo o repo (fora obj/bin) — só existem 4 `.csproj`
reais no repo (`LayoutParserApi.csproj`, `tools/LowCodeRunner`, `mcp/LayoutParserMcp`, `ai/XslSynth`);
nenhum projeto de teste dedicado hoje, então não há suíte de teste pra atualizar por causa disso.

**Tier 1 — remove agora, zero downstream (confirmado: nenhum consumidor em lugar nenhum):**
- `Services/Generation/Implementations/AIService.cs` + `Services/Generation/Interfaces/IAIService.cs`
  (OpenAI) — grep no repo inteiro por `IAIService`/`AIService` só encontra o próprio par arquivo/interface.
  Nada mais injeta isso. Remoção trivial.
- Seção `OpenAI` do `appsettings.json`.

**Tier 2 — precisa decidir o substituto ANTES de apagar (3 consumidores reais de `GeminiAIService`):**
- `Controllers/XmlAnalysisController.cs` (`AnalyzeXsdErrorWithAi`) → vira o novo endpoint Ollama (itens 3-5,
  já decidido).
- `Services/Generation/Implementations/SyntheticDataGeneratorService.cs` (`GenerateWithGeminiAsync`) → ver
  Decisão 3 em [[gemini-openai-decommission-decision]] (fork open-source ou, no meio tempo, só usar o
  fallback `GenerateWithRulesAsync` que já existe e já funciona sem IA).
- `Services/Generation/TxtGenerator/Generators/SemanticAIGenerator.cs` → mesmo caso.
- Só depois de resolver os 3 acima: apagar `GeminiAIService.cs` e a seção `Gemini` do `appsettings.json`.

**Achado à parte, bug pré-existente NÃO relacionado à decisão de nuvem (encontrado no caminho):**
- `Controllers/RAGController.cs` injeta `RAGService` direto no construtor — **também não sobe hoje**, mesmo
  motivo (RAGService não registrado no DI). Mas `RAGService.cs` não tem nenhuma dependência de
  Gemini/OpenAI/HttpClient (é retrieval puro) — corrigir o registro dele é independente da decisão de
  decomissionar nuvem, é só um bug de DI separado. Sinalizar a Dex como achado incidental, não pré-requisito
  da remoção.
- `Services/Generation/TxtGenerator/TxtFileGeneratorService.cs` usa
  `serviceProvider.GetService<SemanticAIGenerator>()` (não injeção direta no construtor) — já é
  resiliente/opcional no padrão do projeto (retorna null se não registrado, não derruba nada). Risco baixo
  de tocar.

**Dívida de documentação (achado, ação de `@lp-doc`, não minha nem do Dex):** Gemini/OpenAI aparecem em
`README.md` (8 lugares: badge, diagrama do ecossistema, tabela de stack, tabela de config, exemplos de
user-secrets, texto de remediação de segurança, comentário de estrutura de pastas), `.claude/rules/
security.md` (5 lugares, incluindo a própria regra "LLM em nuvem (Gemini/OpenAI)" — considerar generalizar
pra "qualquer LLM em nuvem" já que não sobra nome de produto nenhum no código) e `.claude/CLAUDE.md` (persona
da Lia cita "Ollama/Gemini"). Ficam desatualizados assim que o código sair — registrar como tarefa pro
`@lp-doc` (Duda), não fazer busca-e-substitui automática sem revisão editorial.

## How to apply

Ao implementar os itens 3-5 (serviço Ollama novo), NÃO restaurar `GeminiAIService`/`AIService` como parte do
"conserto" do DI — o conserto correto é registrar as substitutas Ollama, não reativar Gemini. Antes de
reativar `DataGenerationController`/geração sintética por IA, decidir com o dono do projeto se essa feature
ainda é desejada e em que nível de fidelidade (ver Decisão 3 em [[gemini-openai-decommission-decision]]) — o
fallback `GenerateWithRulesAsync` baseado em regra, sem IA, já existe e funciona sozinho, não assumir que
precisa de modelo nenhum só porque o código antigo usava.
