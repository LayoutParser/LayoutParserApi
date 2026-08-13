---
description: Matriz de autoridade e delegação entre os agentes do LayoutParser API.
---

# Agent Authority — LayoutParser API

## Matriz de delegação

### @lp-devops (Gage) — Autoridade EXCLUSIVA

| Operação | Exclusivo? | Outros agentes |
|----------|-----------|----------------|
| `git push` / `git push --force` | SIM | BLOQUEADO |
| `gh pr create` / `gh pr merge` | SIM | BLOQUEADO |
| Editar `.github/workflows/`, `Dockerfile` | SIM | BLOQUEADO |
| Adicionar/configurar MCP (`.mcp.json`) | SIM | BLOQUEADO |
| Rotação/migração de segredos | SIM | BLOQUEADO |
| `gh pr create` / `gh pr merge` | SIM | BLOQUEADO (inclusive `@lp-pm`) |

### @lp-pm (Pia) — Backlog / GitHub Project

| Possui | Não possui |
|--------|-----------|
| `gh issue create/edit`, `gh project item-add` — PBI/User Story/bug/gate viram item de board | `gh pr create/merge`, `git push` → `@lp-devops` |
| Traduzir achado/decisão de qualquer agente em item rastreável | Decidir prioridade final, cortar escopo → dono |
| Buscar issues existentes antes de criar (evitar duplicata) | Código de produção, CI/infra/segredos → `@lp-backend-dev`/`@lp-devops` |

`gh issue`/`gh project` ficam com `@lp-pm` porque são backlog (produto), não release —
diferente de `gh pr`, que é o portão de release e continua exclusivo do `@lp-devops`.

### @lp-architect (Aria) — Design

| Possui | Delega para |
|--------|-------------|
| Decisões de arquitetura e tecnologia | — |
| Visão IA→XSLT (desenho) | `@lp-parser-llm` (implementação) |
| Especificação das tools do MCP | `@lp-devops` (registro) / `@lp-backend-dev` (código) |
| **NÃO** escreve código de produção | `@lp-backend-dev` / `@lp-parser-llm` |

### @lp-backend-dev (Dex) — Implementação

| Permitido | Bloqueado |
|-----------|-----------|
| `git add`, `git commit`, `git status`, `git diff` (local) | `git push` → `@lp-devops` |
| Criar/editar controllers, services, DI | `gh pr create/merge` → `@lp-devops` |
| Branch/checkout/merge local | Editar CI/Dockerfile/MCP → `@lp-devops` |

### @lp-parser-llm (Lia) — Domínio parsing/IA

| Possui | Não possui |
|--------|-----------|
| Parsing, detecção, Learning/RAG, geração XSLT/TCL | Infra/CI/git push |
| Integração Ollama/Gemini/OpenAI | Arquitetura macro (delega a `@lp-architect`) |

### @lp-qa (Quinn) — Qualidade

| Possui | Não possui |
|--------|-----------|
| Quality gates, testes, veredito PASS/FAIL | Implementar a correção (devolve a dev) |
| Validação de transformação (XSD/diff) | git push |

### @lp-doc (Duda) — Documentação

| Possui | Não possui |
|--------|-----------|
| README bilíngue, Swagger/XML docs, diagramas | Código de produção · git push |

## Fluxos de delegação

```
Feature:    @lp-architect (desenha) → @lp-backend-dev / @lp-parser-llm (implementa)
            → @lp-qa (valida) → @lp-doc (documenta) → @lp-devops (push)

Git push:   QUALQUER agente → @lp-devops *push

Segredos:   QUALQUER agente detecta → @lp-devops *secure-secrets

Backlog:    QUALQUER agente acha bug/gate/pendência → @lp-pm formaliza no GitHub Project
            (rascunho → confirmação → gh issue create), dono decide prioridade
```

## Escalonamento

1. Agente não consegue concluir → escalar ao usuário com contexto.
2. Quality gate falha → retorna ao dev com feedback específico.
3. Segredo/credencial detectado → BLOQUEIA commit, aciona `@lp-devops`.
