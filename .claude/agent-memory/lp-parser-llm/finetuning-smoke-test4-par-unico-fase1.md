---
name: finetuning-smoke-test4-par-unico-fase1
description: Smoke-test #4 (2026-08-29) — chunking + treino completo de 1 único par tcl/xsl real, bug de truncamento de prompt achado e corrigido, geração pós-treino mostra defaults semânticos corretos
metadata:
  type: project
---

Continuação de [[finetuning-smoke-test3-checkpointing-chunking]]. Mudança de escopo pedida pelo
dono: em vez de medir chunking do dataset inteiro (56,6h, inviável), treinar sobre **1 único par
`.tcl`/`.xsl` completo** (`NFe/4.00/NFe009_4.00_EnvioNFe_NeoGridToSefaz`, `.xsl`=41.901 tokens) —
"ver funcionando" o mapeamento específico antes de generalizar.

**Bug real achado antes do treino valer a pena:** `smoke_train_real3.py` (usado nos smoke-tests
#2/#3) não truncava o prompt (`.tcl` bruto) antes de concatenar com a completion e truncar o texto
final a `MAX_LEN`. Como o prompt sozinho (10.101 tokens) já excede `MAX_LEN=2048`, a truncação
final comia 100% da completion — os exemplos treinariam com zero tokens de XSLT real, loss caindo
"normalmente" mascarando dado de treino totalmente errado. Corrigido truncando o prompt a
`MAX_LEN//2` ANTES de concatenar (mesma premissa que `build_chunks.py` já assumia no cálculo de
budget, mas o script de treino não replicava). **Gotcha geral:** em qualquer pipeline
prompt+completion com truncamento por tamanho total, nunca confiar em truncar o texto já
concatenado — truncar o prompt isoladamente primeiro.

**Treino real (pós-fix):** 57 chunks (janela 2048, overlap 256) cobrindo o `.xsl` inteiro, 1 época,
gradient checkpointing, MAX_LEN=2048. **6.894s (1h55min) de treino puro, sem OOM, RSS estável
~11,64GB.** ~120-122s/passo (mesmo padrão dos smoke-tests #2/#3). Loss 0,7647→0,5935. Extrapolação
honesta pra 3 épocas: ~5h45min (não executado, fora do orçamento da sessão) — MUITO diferente da
escala do dataset inteiro (24,8h-56,6h no smoke-test #3), porque é 1 par só.

**Validação por geração real:** com o adapter LoRA treinado, gerou XSLT a partir do `.tcl` de
entrada. Primeiros ~1024 tokens NÃO são XSLT (ecoa estilo `.tcl` de entrada — modelo ainda não
aprendeu a transição prompt→resposta com só 1 época). A partir daí, converge pra XSLT real e
válido, com **defaults semânticos idênticos ao `.xsl` real** (`tpAmb=2`, `tpEmis=1`, `procEmi=0`)
— sinal real de aprendizado do mapeamento específico, não só sintaxe XSLT genérica. Não comparado
por diff formal, só inspeção visual (como pedido).

**Why:** valida que a "Fase 1 reduzida" (1 mapeamento completo) é viável nesta VM CPU-only e já
mostra sinal de aprendizado real — sem essa validação, não dava pra saber se o ciclo
dado→chunk→treino→geração realmente ensina algo específico do mapeador ou só sintaxe genérica.
**How to apply:** antes de rodar QUALQUER treino "de verdade" (múltiplas épocas, dataset maior),
replicar o teste rápido de 3 exemplos pra confirmar que a completion não está sendo truncada — é
barato (~6min) e teria pego o bug antes de qualquer smoke-test anterior perder tempo de treino real
com dado errado.

Scripts na VM (não commitados, artefatos de sessão): `~/build_chunks_single.py`,
`~/smoke_train_single_pair.py`, `~/infer_single_pair.py`, dataset `~/single_pair_chunked.jsonl`,
adapter `~/lora_single_pair_adapter/`, saída `~/gerado_single_pair.xsl`.
