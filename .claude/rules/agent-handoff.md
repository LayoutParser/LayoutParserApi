---
description: Protocolo de handoff (compactação de contexto) ao trocar de agente.
---

# Agent Handoff — LayoutParser API

## Propósito

Evitar acúmulo de contexto ao alternar entre agentes (`@lp-*`). Em cada troca, o
agente que sai é compactado num **artefato de handoff (~400 tokens)** em vez de
manter sua persona completa.

## Quando se aplica

Sempre que: (1) o usuário invoca um novo agente `@lp-*`, e (2) já havia outro agente ativo.

## Artefato de handoff

Ao sair, gere mentalmente:

```yaml
handoff:
  from_agent: "{agente_atual}"
  to_agent: "{novo_agente}"
  contexto:
    tarefa: "{tarefa/missão em andamento}"
    branch: "{branch git atual}"
    arquivos_tocados: ["{arquivo 1}", "{arquivo 2}"]   # máx. 10
  decisoes:                                            # máx. 5
    - "{decisão-chave 1}"
  bloqueios: ["{bloqueio ativo, se houver}"]           # máx. 3
  proximo_passo: "{o que o agente que entra deve fazer}"
```

## O que SEMPRE preservar
- Tarefa/missão atual e branch
- Arquivos criados/alterados
- Decisões arquiteturais ou de domínio relevantes
- Bloqueios ativos e próximo passo

## O que SEMPRE descartar
- Persona completa do agente anterior
- Lista de tools/missões do agente anterior
- Instruções de contexto já absorvidas

## Limites

| Limite | Valor |
|--------|-------|
| Tamanho do artefato | ~500 tokens |
| Resumos retidos | 3 (o mais antigo é descartado no 4º) |
| Decisões | 5 |
| Arquivos | 10 |
| Bloqueios | 3 |

## Exemplo

`@lp-architect` desenha a melhoria de RAG → troca para `@lp-parser-llm`:
- persona da arquiteta (~3K tokens) é **descartada**;
- handoff (~400 tokens) é **retido**: decisão (vector store), arquivos, próximo passo;
- persona da Lia é **carregada**. Economia ~80% por troca.
