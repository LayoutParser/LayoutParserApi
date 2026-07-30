---
name: finetuning-poc-fase2-filtro-v2-e-rag-spike
description: Correcao do filtro de dataset por-tipo-de-mensagem (CTe deixou de ser 2 pares e virou 15) + spike de RAG/few-shot sem fine-tuning
metadata:
  type: project
---

Sessao 2026-07-29, duas tarefas sequenciais do POC de fine-tuning (ver [[finetuning-poc-fase1-dataset]]
e a QA da Quinn em `lp-qa/finetuning-poc-fase1-dataset-qa.md`).

## Tarefa A — filtro v2 (correcao do artefato Neogrid)

O filtro v1 (`filter_dataset.py`) mantinha so a "versao final do DOCUMENTO" (NFe 4.00/4.000,
CTe 4.00, MDFe 3.00) — derrubou CT-e para so **2 pares**. O usuario apontou (confirmado no
corpus) que isso e um artefato do processo Neogrid: quando a regra de um EVENTO especifico nao
muda entre versoes, a pasta de versao nova nunca e recriada pra ele — o schema antigo continua
vigente (mesmo padrao ja visto no bug `NFe008a_CancNFe_NeoGridToSefaz.xsl` com `versao="2.00"`
hardcoded numa pasta 4.00; achei uma SEGUNDA instancia do mesmo padrao nesta sessao:
`CTe200_CancCTe_NeogridToSefaz.xsl` em `CTe/2.00a` tem `versao="1.04"` hardcoded).

**Fix:** `filter_dataset_v2.py` agrupa por `(doc_type, tipo_de_mensagem_normalizado)` — tipo
extraido removendo o prefixo de codigo+versao do stem (regex `^(?:NFe|CTe|MDFe)[0-9]*[a-z]?(?:\.[0-9]+)?_`
+ segundo pass pra token de versao residual tipo `4.00_`/`400_`/`2.06_`). Para cada tipo, mantem
a versao mais recente DISPONIVEL PARA AQUELE TIPO especificamente (nao a versao mais recente do
documento como um todo). Variantes de pipeline (`NeogridToSefaz` vs `NeoGridPipelineToSefaz` vs
`NeogridToSefazAGV`) sao tratadas como tipos DISTINTOS (integracoes diferentes de verdade, nao
so nome de versao).

**Resultado real** (`dataset_pairs_filtered_v2.jsonl`, 54 pares — antes eram 39):
- CTe: **15** pares (era 2) — 15 tipos distintos de mensagem. Exemplos recuperados: CancCTe/
  ConsSitCTe/ConsStatServ ficaram em **2.00a** (nunca recriados depois); EnvioCTe/RetEnvCTe em
  **4.00**; InutCTe/RetCancCTe/RetInutCTe/RetCTe(pipeline) em **3.00**.
- MDFe: 6 (igual ao v1 — MDFe so tem 1.00c/3.00 e todo tipo existe em 3.00).
- NFe: 33 (era 31) — 18 tipos distintos; ConsCad/RetConsCad ficaram em **3.10** (nunca migrado
  pra 4.00); `evtCancNFe` so existe em 4.00 (tipo novo, sem precedente). NFe continua com
  duplicacao 4.00/4.000 pro mesmo tipo em quase todos os casos (mesma nota do v1: nao sao
  identicos, sao 2 revisoes reais da mesma versao nominal).

**QA (amostragem manual desta sessao):** 4 pares novos lidos completos (2 CTe/3.00: InutCTe +
RetCancCTe; 2 CTe/2.00a: CancCTe + tokenizacao geral) — todos batem semanticamente (campo do
`.tcl` aparece no `xsl:value-of select` do `.xsl` correspondente). Amostra mais robusta que a
Fase 1 pediu para CT-e (agora 15 pares reais, nao 2) — a ressalva de "baixa confianca CT-e" da
QA original **fica superada** por esta correcao.

Scripts em `.claude/tmp/dataset-finetuning/`: `filter_dataset_v2.py`, saida
`dataset_pairs_filtered_v2.jsonl` + `filter_report_v2.json` (detalhe por tipo: versoes
disponiveis vs mantidas). `filter_dataset.py` (v1) preservado intacto, nao sobrescrito.

## Tarefa B — spike de RAG/few-shot (sem fine-tuning)

Mecanismo de recuperacao: **TF-IDF manual em stdlib puro** (Counter + math.log, sem sklearn —
nao instalado neste ambiente; corpus de ~50 docs curtos nao justifica puxar dependencia pesada
so pro spike). Tokens = nomes de `FIELD name=`/`LINE identifier=` extraidos do `.tcl` via regex.
Cosine similarity pra rankear os N mais parecidos dentro do MESMO doc_type.

**Held-out real:** ao testar um tipo de mensagem, excluido da pool de recuperacao TODO registro
do MESMO tipo (nao so o registro exato) — varios tipos NFe tem copia quase identica em 4.00/4.000;
excluir so uma copia seria trapaca (recuperaria o gemeo quase identico como few-shot "livre").

**Modelo usado:** `qwen2.5-coder:7b` via Ollama local — **SUBSTITUICAO DOCUMENTADA** do
`deepseek-coder:6.7b` configurado em producao (`appsettings.json` `Ollama:Model`): o
`deepseek-coder:6.7b` NAO esta puxado neste ambiente (`ollama list`/`/api/tags` so mostra
`qwen2.5-coder:7b`, ~4.7GB). Puxar um modelo novo de ~7GB so pro spike nao pareceu
justificado dado o gate ainda fechado; resultado deve ser lido como "sinal de RAG few-shot
generico com um coder model de porte semelhante", nao uma validacao 1:1 do modelo de producao.

Casos de teste (held-out, escolhidos pequenos — CPU-only e lento, ver [[cte-synthetic-cpu-timeout-2026-07-28]]):
- CTe: `consstatservcte_neogridtosefaz` (gabarito `CTe200_consStatServCTe_NeogridToSefaz`, 2.00a)
- MDFe: `conssitmdfe_neogridtosefaz` (gabarito `MDFe300a_ConsSitMDFe_NeoGridToSefaz`, 3.00)
- NFe: `conssitnfe_neogridtosefaz` (gabarito `NFe009_4.00_ConsSitNFe_NeoGridToSefaz`, exclui 4.00 E 4.000)

Script: `rag_fewshot_spike.py` no scratchpad da sessao (nao versionado no repo — script de spike
exploratorio, nao codigo de producao). Saida: `rag_fewshot_spike_results.json` no mesmo scratchpad.

[PREENCHER apos rodar: metricas reais (tag_overlap_ratio, text_similarity_ratio, well_formed) e
veredito de gate aberto/fechado para fine-tuning.]
