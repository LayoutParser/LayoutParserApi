---
name: rbac-scope-xml-generic-2026-08-14
description: Desenho de RBAC real (admin=governança de mapeador, não "quem vê") + XML->XML já existe em código + isolamento de IA por usuário como pré-requisito de abrir RBAC
metadata:
  type: project
---

Desenho registrado em `docs/architecture/escopo-generico-txt-xml-e-acesso-por-papel-2026-08-14.md`
(2026-08-14), disparado por um 403 real em `execute-candidates`. Pontos que não são óbvios a partir
do código sozinho:

- **`admin` não é "quem pode ver a transformação"** — o dono corrigiu isso ao vivo durante a sessão:
  `admin` é escopo de **governança sobre artefatos de mapeamento já gerados** (editar/promover/
  revogar TCL/XSL, promover candidato IA a "oficial"). Hoje **não existe nenhum endpoint de escrita
  sobre mapeador** nem mecanismo de "promover candidato IA convergido a mapeador oficial do
  catálogo" — é trabalho novo a desenhar, não um `[Authorize]` a mover. `GET export/{id}`
  (`MapperDatabaseController`) expõe `DecryptedContent` sem `[Authorize]` hoje — achado incidental,
  pendente de decisão do dono se deve virar `admin`.
- **XML→XML já é código real, não aspiracional.** `isXmlInput` é detectado automaticamente
  (`InputContent.TrimStart().StartsWith("<")`), `TransformationPipelineService.TransformXmlToXmlAsync`
  existe e roda via `XslCompiledTransform`. O que falta é abrir RBAC + revisar `FindXslFile` (usa
  `sourceType`/`targetType` de fato na busca, ou só loga? não confirmado nesta sessão).
- **Vazamento de confidencialidade entre usuários é criado pela própria mudança de RBAC pedida.**
  `AiCandidateStore` é `ConcurrentDictionary<string, StoredEntry>` global chaveado só por `ticket`
  (derivado de conteúdo+layout, sem entropia de usuário) — hoje inofensivo porque só `admin` (poucas
  contas) acessa `ia-status`/`execute-candidates`. Abrir esses endpoints a "qualquer usuário
  autenticado" (pedido do dono) sem antes particionar o store por `ICurrentUser.Name` cria
  vazamento real entre usuários. Recomendação: isolamento por usuário é **pré-requisito bloqueante**
  da abertura de RBAC, não trabalho paralelo.
- Ver também [[gemini-openai-decommission-decision]] (Ollama local, sem nuvem) e a fronteira do
  XSLT já mapeada em `docs/architecture/viabilidade-dlls-sysmiddle-para-rag.md` §5 (reaproveitada
  neste desenho, não fingir que XSLT cobre I/O externo/estado mutável complexo).
- Prompt customizado do usuário: recomendado como campo de **sessão** (não parâmetro solto por
  chamada), anexado depois do prompt de sistema fixo (nunca substituindo), mitigado pelo verificador
  determinístico já existente (`CanonicalDiffer`+XSD) que não depende do LLM "se comportar".

**Why:** três requisitos novos chegaram como correção ao vivo durante a escrita do documento — não
tratar como tarefa separada da próxima sessão, o desenho já foi consolidado num único documento.

**How to apply:** antes de aprovar implementação da abertura de RBAC em `execute-candidates`/
`ia-status`/`execute-lowcode`, confirmar que o isolamento por usuário do `AiCandidateStore` (Passo 1
do §7.2 do desenho) já está feito ou entrando junto — não como follow-up.
