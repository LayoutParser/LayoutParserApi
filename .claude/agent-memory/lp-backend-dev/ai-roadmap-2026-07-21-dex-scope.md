---
name: ai-roadmap-2026-07-21-dex-scope
description: Estado do roadmap de IA (docs/architecture/ai-roadmap-dispatch.md) do lado de @lp-backend-dev — o que ficou deliberadamente parado e por quê, após a rodada de 2026-07-21.
metadata:
  type: project
---

Sessão de arquitetura de 2026-07-21 consolidou um roadmap de IA em
`docs/architecture/ai-roadmap-dispatch.md` (+ `ia-fiscal-diagnosis-vision.md`
para o detalhe do diagnóstico fiscal). Executei o subconjunto liberado sem
dependência pendente (itens 1.1, 1.4, 2.1, 3.1, 3.2, 3.6 — ver `git log` para
os commits, mensagens começam com "refactor: remove integracao morta com
OpenAI", "fix: registra RAGService...", "docs: formaliza Pathway 2...", "chore:
prepara script de benchmark..."). Dois blocos ficaram **deliberadamente**
parados, não por esquecimento:

- **1.2/1.3** (decidir substituto dos 3 consumidores de `GeminiAIService`,
  depois apagá-lo): bloqueado até os Grupos 3/4 (diagnóstico fiscal via
  Ollama, dado sintético) terem destino concreto para esses consumidores. Não
  registrei `GeminiAIService` no DI mesmo ele estando quebrado em runtime hoje
  (ver [[generation-services-unregistered-di]]) — registrá-lo agora seria
  comprometer-se com a decisão pendente de decommission.
- **3.4/3.5** (serviço Ollama real + endpoint de diagnóstico): depende dos
  itens 3.1-3.3 prontos (3.1/3.2 já existem como serviços standalone em
  `Services/Validation/`, mas ainda não wired em nenhum endpoint - fiação real
  é o próprio 3.4/3.5) e do sidecar de proveniência da Lia (Grupo 5/A6).

Achado incidental fora do escopo desta rodada, reportado mas não corrigido:
`DataGenerationController` também quebra em runtime por falta de registro de
DI (mesma classe de bug do RAGController, mas nunca foi dispatchado por
ninguém) - ver [[generation-services-unregistered-di]].

**Why:** evita repetir 1.1/1.4/2.1/3.1/3.2/3.6 (já feitos) ou começar
1.2/1.3/3.4/3.5 antes da hora certa numa sessão futura.

**How to apply:** antes de continuar este roadmap, reler
`docs/architecture/ai-roadmap-dispatch.md` (pode ter avançado desde então) e
confirmar se as dependências de 1.2/1.3/3.4/3.5 já fecharam.
