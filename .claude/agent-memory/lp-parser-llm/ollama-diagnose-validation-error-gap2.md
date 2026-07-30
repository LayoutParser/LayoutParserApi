---
name: ollama-diagnose-validation-error-gap2
description: Implementação do Gap 2 (diagnose-validation-error via Ollama) — decisões de design não especificadas no contrato, um bug real de timeout encontrado/corrigido, e limite honesto de teste manual (CPU-only local).
metadata:
  type: project
---

Implementado `POST /api/xml-analysis/diagnose-validation-error` (2026-07-28) conforme
`docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md` (Gap 2). Arquivos:
`Services/XmlAnalysis/OllamaOptions.cs`, `ValidationDiagnosticModels.cs`,
`OllamaValidationDiagnosticService.cs`, `Controllers/ValidationDiagnosticController.cs`,
registro em `Program.cs` (grupo "XML Analysis Services"), config nova
`Ollama:DiagnosisTimeoutSeconds` em `appsettings.json`.

**Decisão não especificada no contrato — controller dedicado, não `XmlAnalysisController`:**
descobri que `XmlAnalysisController` inteiro está quebrado em runtime hoje — depende de
`GeminiAIService` no construtor, que não está registrado no DI (confirmado por grep em
`Program.cs`, zero resultado). Isso bate com o achado já registrado em
`.claude/agent-memory/lp-backend-dev/generation-services-unregistered-di.md` do Dex. Qualquer
request pra esse controller (incluindo um endpoint novo que eu adicionasse nele) daria erro de
ativação de DI. Por isso criei `ValidationDiagnosticController` novo, na mesma rota base
`api/xml-analysis` pedida pelo front, mas em classe C# separada — não depende de
`GeminiAIService`. Não tentei consertar o registro do `GeminiAIService`/cluster `Generation`
inteiro (fora do escopo do Gap 2, e o Dex já sinalizou deliberadamente não mexer nisso até a
decisão de decommission fechar — que já fechou, então isso virou dead code candidato a remoção,
tarefa do Dex).

**Como o `Confidence` numérico é extraído do modelo:** usei o suporte a saída estruturada do
Ollama (campo `format` com JSON Schema no corpo de `/api/generate`, não `format: "json"` genérico)
— confirmado funcionando nesta instância real (Ollama v0.31.2, testado via curl direto).
Schema pede `{summary: string, suggestedFix: string, confidence: number}`. Duas decisões que não
estavam no contrato:
1. **Sem union type `["string","null"]`** no schema do `suggestedFix` — não validei que este
   Ollama suporta nullable no JSON Schema, então pedi `""` via prompt quando não há sugestão e
   trato string vazia como `null` no parse. Mais seguro que arriscar o schema inteiro falhar.
2. **Fallback de parsing de texto livre**: se o modelo não respeitar o schema (JSON malformado),
   trato a resposta inteira como `summary` com `confidence = 0.3` fixo — nunca lança exceção por
   isso (baixa confiança/parse falho não é erro HTTP, é dado com confiança baixa, conforme a
   tabela de decisão do contrato).

**Bug real encontrado e corrigido — timeout duplo/concorrente:** `AddHttpClient<T>()` sem
configuração explícita registra `HttpClient` com `Timeout` default de **100s**, que corre em
paralelo com o `CancellationTokenSource` próprio do serviço (`Ollama:DiagnosisTimeoutSeconds`).
Quando o timeout do `HttpClient` dispara primeiro, gera `TaskCanceledException` envolvendo
`TimeoutException` que meu catch original (`catch (OperationCanceledException) when
(timeoutCts.IsCancellationRequested)`) **não reconhecia** — caía no catch genérico do controller
e virava 500 em vez do 504 esperado pelo contrato. Corrigido em duas frentes: (1)
`client.Timeout = Timeout.InfiniteTimeSpan` no `AddHttpClient` em `Program.cs` — só o meu
`CancellationTokenSource` deve governar o timeout; (2) o catch de cancelamento no serviço virou
incondicional (`catch (OperationCanceledException)`, sem `when`), tratando qualquer cancelamento
(inclusive desconexão do cliente HTTP) como `DiagnosticFailureKind.Timeout` — fallback mais seguro
que deixar cair no 500 genérico. Verificado via teste manual: 504 correto após o fix.

**Bug de dado real encontrado no próprio modelo — escala de confidence:** apesar do prompt pedir
explicitamente "confidence (número 0.0 a 1.0)", o modelo (`qwen2.5-coder:7b`, mesmo padrão
esperado no `deepseek-coder:6.7b` configurado) devolveu `"confidence": 90` em vez de `0.9` num
teste real. Um `Math.Clamp(0,1)` ingênuo teria truncado isso pra `1.0`, destruindo o sinal real.
Corrigido em `ParseModelResponse`: se `confidence > 1.0`, assume escala 0–100 e normaliza
(`/100.0`) antes do clamp final. **Isso é uma heurística, não uma garantia** — se um usuário
futuro trocar de modelo, vale reconfirmar que a heurística ainda faz sentido (um modelo mais
obediente ao schema pode nunca disparar esse caminho).

**Teste manual — o que foi validado e o que ficou honestamente incompleto:** confirmado 200 (com
schema estruturado funcionando, prompt curto isolado), 400 (`ErrorMessage` vazio), 503 (Ollama
apontando pra porta inexistente), 504 (timeout, incluindo com o bug do `HttpClient.Timeout`
corrigido). **NÃO consegui fechar um round-trip de sucesso (200) através do endpoint real da API
com o prompt de produção completo** (o `BuildPrompt` real, mais verboso que o teste isolado) —
mesmo com o modelo já "quente" (`/api/ps` confirmando carregado), a chamada excedeu 150s
consistentemente. Isso é atribuível ao ambiente (Ollama CPU-only, sem GPU — ver decisão de
topologia de produção na memória do `@lp-devops`/`@lp-architect`: só a API roda no Windows Server,
Ollama fica numa VM Ubuntu sem GPU), não a um defeito identificado no código: o mesmo mecanismo
(schema + parse) foi comprovado funcionando isoladamente via curl direto ao Ollama, só que com
prompt mais curto. **Recomendação para quem for validar em produção/staging com GPU real:** rodar
o teste de novo lá — se ainda demorar >60-90s pra prompt real, considerar reduzir o timeout default
atual (60s) só depois de medir latência real em hardware de produção, e/ou enxugar o `BuildPrompt`
(hoje inclui várias linhas de instrução fixas que talvez não precisem ir em toda chamada).

**Config real do ambiente de teste:** `Ollama:Model` em `appsettings.json` está
`deepseek-coder:6.7b`, mas **esse modelo não está pull'ado localmente** — só `qwen2.5-coder:7b`
existe no Ollama desta máquina (`curl /api/tags`). Usei `qwen2.5-coder:7b` via env var
`Ollama__Model` só para os testes manuais desta sessão; não alterei o `appsettings.json`. Se
alguém for rodar isso de verdade, `ollama pull deepseek-coder:6.7b` primeiro (ou trocar a config
pro modelo que já existe).
