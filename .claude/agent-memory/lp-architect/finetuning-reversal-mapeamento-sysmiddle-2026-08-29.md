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

## Correção de rumo no mesmo dia — não subdimensionar modelo pelo hardware de inferência

O dono corrigiu a primeira versão desta entrega (que recomendava 1-3B por causa do
[[production-server-hardware]] CPU-only): a prioridade é um modelo **tecnicamente correto pro
domínio** (7B-14B, faixa realista pra raciocínio estrutural XML/XSLT), mesmo que o host de
produção atual não aguente rodar bem. Se o hardware não aguentar, isso vira **pendência de infra
separada** (upgrade ou host alternativo) — não motivo pra escolher um modelo pior. Regra geral a
aplicar daqui pra frente: **dataset pequeno** dita a técnica de treino (LoRA/QLoRA, não fine-tuning
completo) e **onde treinar** (sempre offline, nunca no host de produção) — mas não deve ditar
**o tamanho do modelo final**; isso é decidido pela exigência da tarefa, e a lacuna de hardware de
inferência é discutida à parte, explicitamente, sem contaminar a escolha do modelo.
