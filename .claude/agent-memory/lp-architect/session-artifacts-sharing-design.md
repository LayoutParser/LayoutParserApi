---
name: session-artifacts-sharing-design
description: Desenho de sessao de usuario vs artefatos compartilhados, refinando issues #92/#97 (2026-08-14)
metadata:
  type: project
---

Documento novo `docs/architecture/sessao-usuario-e-artefatos-compartilhados-2026-08-14.md`
complementa [[track-a2-a5-spec]]-adjacent doc-mae
`escopo-generico-txt-xml-e-acesso-por-papel-2026-08-14.md` (branch
`docs/rbac-generico-e-resposta-frontend-2026-08-14`, PR #100).

**Decisões-chave registradas:**
- Sessão (rascunho/histórico de trabalho de um usuário) e artefato promovido (TCL/XSL/XSLT
  validado, conhecimento institucional) são camadas distintas com ciclo de vida diferente —
  não tratar como a mesma coisa mesmo em "ferramenta interna, tudo é da NDD".
- Recomendação: isolamento por usuário por padrão (default-deny entre usuários) mesmo em
  modelo "tudo é da empresa" — não é sigilo entre concorrentes, é higiene de UX/responsabilidade
  (rascunho de outro não deve aparecer sem contexto). Compartilhamento acontece via **promoção**
  explícita (ação de admin, já mapeada em §6.1 do doc-mãe), não por default-allow de ticket em
  progresso. Isto refina, não substitui, a issue #92 — o Passo 1 (particionar `AiCandidateStore`
  por `ICurrentUser.Name`) continua correto como pré-requisito.
- Modelo de persistência: extensão do que existe para isolamento (`AiCandidateStore` + dimensão
  `userId`, continua cache/TTL curto — natureza de rascunho efêmero está certa). Histórico de
  longo prazo é conceito NOVO — tabela SQL nova (`AiUserSession`/`AiUserSessionHistoryEntry`),
  porque "SQL é fonte da verdade" não deveria virar "arquivo com TTL promovido a permanente
  disfarçado de cache". Tabela guarda referência/status, não duplica conteúdo pesado (XSLT/TCL).
- Analogia Claude Code/Codex/ChatGPT: aplica-se a "identidade persistente com histórico e
  preferências", NÃO a chat multi-turno — pathway de IA aqui é single-shot por ticket, sem
  memória entre chamadas Ollama. Isto refina a issue #97 para escopo realista: histórico +
  prompt persistente (já desenhado no doc-mãe §8) + retomada pontual de ticket falho — não
  infraestrutura de conversa livre.
- Geração de mapeamento a partir de layout+gabarito SEFAZ+lógica fiscal: **capacidade nova**,
  não extensão simples do RAG atual — reconecta com achado prévio (XSLT é fraco para estado
  cross-seção/lógica de negócio complexa, ver `viabilidade-dlls-sysmiddle-para-rag.md` §5,
  citado no doc-mãe §4). Recomendação: two-step (LLM deriva regra estruturada e auditável por
  humano → depois gera XSLT a partir da regra, não do exemplo cru) em vez de pedir as duas
  coisas numa chamada só a um modelo pequeno CPU-only. Não corresponde a issue existente —
  item de backlog novo, sequenciado depois da estabilização de #92/#93/#97/#98.

**Como aplicar:** se o dono ou `@lp-pm` trouxer #92/#97 para trabalhar, checar se este
documento já foi incorporado à redação das issues — se não, é o gap a fechar primeiro
(divergência entre o que a issue diz e o que foi decidido aqui).
