---
name: finetuning-reversal-mapeamento-sysmiddle-2026-08-29
description: Dono reverteu a decisao "sem fine-tuning" de 21/07 — pedido novo é fine-tuning especializado pra recriar mapeamentos Sysmiddle (TXT->TCL->XSL->XML e XML->XSL->XML), com observabilidade do processo
metadata:
  type: project
---

Em 2026-08-29, o dono pediu fine-tuning direto (não RAG-só) pra um objetivo específico: o Ollama
"aprender e virar especialista em criar novos mapeamentos" — primeiro estágio é recriar os
mapeamentos Sysmiddle já existentes, dois pathways (TXT->TCL->XSL/XSLT->XML e XML->XSL/XSLT->XML),
mais observabilidade do processo de criação (não só o resultado). Isso **reverte** [[gemini-openai-decommission-decision]]
Decisão 2 (21/07: "sem fine-tuning pra diagnóstico XSD") — motivo da reversão é mudança de escopo
do objetivo, não invalidação do raciocínio original (que segue moldando o desenho: dataset pequeno
→ LoRA/QLoRA em vez de fine-tuning completo; verificador determinístico continua obrigatório;
[[production-server-hardware]] CPU-only → treino sempre offline, só inferência local).

Plano completo entregue em `docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md`:
LoRA sobre modelo base 1-3B, um modelo + dois adaptadores (não dois modelos do zero), 3 fases
(replicar conhecido → segundo pathway + generalização controlada held-out → mapeamentos novos),
observabilidade via instrumentação do `RepairOrchestrator` existente (não infra nova de cara).

**Pontos pendentes de autorização explícita do dono** (não avançar implementação sem isso):
uso de serviço de nuvem pra treino (se não houver GPU interna — regra de dado fiscal em nuvem),
qual GPU/orçamento está disponível, escolha final do modelo base (licença), nível de
observabilidade desejado (log vs endpoint vs streaming).

## How to apply

Se o dono voltar a pedir trabalho de IA/diagnóstico XSD "sem fine-tuning", checar se ele está
falando do escopo antigo (diagnóstico, Decisão 2 original) ou do escopo novo (recriar mapeamentos,
esta decisão) — são objetivos diferentes com decisões diferentes, não presumir que uma cancela a
outra automaticamente.

## Correção de rumo no mesmo dia — não subdimensionar modelo pelo hardware de inferência (revertida em seguida)

O dono corrigiu a primeira versão desta entrega (que recomendava 1-3B por causa do
[[production-server-hardware]] CPU-only): a prioridade é um modelo **tecnicamente correto pro
domínio** (7B-14B, faixa realista pra raciocínio estrutural XML/XSLT), mesmo que o host de
produção atual não aguente rodar bem — hardware seria pendência de infra separada.

## Decisão final (mesmo dia, 3ª rodada) — host = VM Ubuntu, CPU-only aceito conscientemente, modelo reduzido de volta pra 1-3B

Encadeamento completo da sessão: (1) modelo dimensionado pelo hardware de inferência do
`BRNDDAPPBLD01` → (2) dono corrige, dimensionar pelo domínio (7B-14B) → (3) ao escolher onde
treinar/rodar, identifiquei que a VM Ubuntu (`UBU220405RUN`, mesma que já roda Ollama pro Job 1 de
métricas) não tem GPU confirmada em nenhuma memória — reportei como bloqueio técnico, sem decidir
sozinho → (4) **dono decide prosseguir mesmo sem GPU**: prioridade da Fase 1 não é
performance/qualidade, é ver o ciclo completo (dado→treino→modelo→geração→validação) funcionando
de ponta a ponta com observabilidade em streaming ao vivo. Otimizar depois.

**Resultado prático:** modelo volta pra 1-3B (não por hardware de inferência em produção, mas por
viabilidade real de TREINO em CPU dentro de uma janela de fim de semana) + LoRA rank baixo.
Streaming ao vivo (SignalR Hub sobre `RepairOrchestrator`) vira parte da Fase 1, não uma evolução
posterior — muda a estimativa de esforço pra cima.

**Lição pra próximas sessões:** as duas "regras" (não subdimensionar por hardware vs. aceitar
hardware real) não se contradizem de fato — a primeira valia quando o objetivo era "modelo
ideal pro domínio"; a segunda entra quando o dono explicita que o objetivo da fase atual é outro
("ver funcionando" > "funcionar bem"). Perguntar qual é o objetivo da fase antes de fixar
qualquer dimensionamento evita reverter a mesma decisão 2x na mesma sessão.
