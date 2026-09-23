---
name: llm-provider-plugavel-2026-09-08
description: Issues #340 (F1 abstração ILlmProvider) e #341 (F2 gap de proveniência) do ADR de @lp-architect sobre LLM plugável; F3/F4 não formalizadas ainda.
metadata:
  type: project
---

Formalizadas 2 issues a partir de `docs/architecture/adr-llm-provider-plugavel-2026-09-08.md`:

- **#340 (F1)** — criar `ILlmProvider`/`DataSensitivity`/`LlmProviderResolver`/`OllamaLlmProvider` em
  `Services/Llm/`, migrar `OllamaValidationDiagnosticService` e `MappingSuggestionService`. Refactor
  de baixo risco, sem provider de nuvem. Dono sugerido: `@lp-backend-dev`.
- **#341 (F2)** — fechar gap de proveniência em `MappingSuggestionService` (campo `ArtifactProvenance`)
  e `SyntheticDataGeneratorService` (isolar prompt com amostra real quando a geração semântica via
  Ollama for religada — hoje esse serviço é 100% regras, sem chamada de LLM ativa). Bloqueada por
  #340, **bloqueia qualquer F3 futura** (primeiro provider de nuvem).

**Não formalizei F3/F4** — ADR recomenda esperar o dono escolher provedor (Anthropic/OpenAI/Kimi2) e
confirmar que aceita F2 como pré-requisito bloqueador, não cosmético.

## How to apply

Se pedirem pra abrir issue de F3/F4 antes de #341 fechar, sinalizar a dependência explícita e
devolver a decisão ao dono — não é minha alçada decidir que F2 já está "bom o suficiente".
