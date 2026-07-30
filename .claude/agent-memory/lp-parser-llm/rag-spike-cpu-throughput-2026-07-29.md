---
name: rag-spike-cpu-throughput-2026-07-29
description: Spike real de RAG few-shot via Ollama (qwen2.5-coder:7b) mediu ~1.3 tok/s de geração em CPU; achado decisivo contra fine-tuning nesta infra até haver GPU.
metadata:
  type: project
---

Spike de RAG few-shot (Fase 2 do POC de fine-tuning, sem fine-tuning) rodado de fato contra
Ollama em 2026-07-29, usando `dataset_pairs_filtered_v2.jsonl` (54 pares: NFe=33, CTe=15,
MDFe=6) como pool de recuperação TF-IDF (held-out por tipo de mensagem, não só por registro
exato — evita "gêmeo" 4.00/4.000).

**Medição direta de throughput** (curl isolado, 30 tokens pedidos): `eval_count=20`,
`eval_duration=15.48s` → **~1.3 tok/s de geração** em `qwen2.5-coder:7b` nesta máquina
CPU-only (a mesma do POC de CT-e sintético, ver [[cte-synthetic-cpu-timeout-2026-07-28]]).
Prefill continua ~16-22 tok/s — ou seja, o gargalo real desta vez NÃO é o tamanho do prompt
(era só 1831 chars / ~450 tokens), é a geração token-a-token em si.

**Resultado real obtido** (único caso completo, `num_predict=180`, 76.3s): caso
`CTe\2.00a\CTe200_consStatServCTe_NeogridToSefaz`, few-shot recuperado por TF-IDF
(similaridade 0.887) foi `CTe200_ConsStatServ_NeogridToSefaz`. Saída gerada ficou
estruturalmente muito próxima do gabarito (`tag_overlap_ratio=0.75`,
`text_similarity_ratio=0.845`) mas com 2 erros semânticos reais: (1) faltou
`xmlns="http://www.portalfiscal.inf.br/cte"` no `xsl:stylesheet`; (2) erro de case no
elemento raiz (`consStatServCTe` gerado vs `consStatServCte` gabarito) — quebraria
validação XSD real. Resultado completo em
`...\scratchpad\rag_fewshot_spike_results.json` (scratchpad de sessão, não persistente).

**Recomendação:** NÃO avançar para fine-tuning nesta infra. Motivo duplo: (a) 1.3 tok/s
torna o loop gerar→validar→corrigir (essencial ao domínio) impraticável em minutos por
tentativa para documentos reais; (b) mesmo com 1 few-shot, já aparecem erros estruturais
previsíveis (namespace, case) que sugerem que few-shot puro sem correção pós-geração via
XSD não basta — e fine-tuning não resolve isso sozinho.

**Why:** decisão de arquitetura já existente é Ollama local first (dado sensível não sai
pra nuvem, ver [[../lp-architect/gemini-openai-decommission-decision.md]]), mas essa
decisão pressupõe throughput viável, que esta medição mostra que NÃO existe em CPU-only.

**How to apply:** antes de propor fine-tuning ou de rodar novos spikes de geração real,
verificar se já há GPU disponível (ver memória de usuário `deploy-production-topology`:
hoje só CPU). Sem GPU, manter o trabalho de RAG/validação em escopo pequeno (poucos tokens
de saída, casos curtos) e não prometer ciclos de geração completos em produção.
