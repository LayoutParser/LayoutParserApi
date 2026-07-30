---
name: finetuning-poc-fase1-dataset-qa
description: QA da Fase 1 do POC de fine-tuning (filtro de versao + amostragem manual do dataset tcl/xsl)
metadata:
  type: project
---

QA aplicado sobre o dataset da Lia (`[[lowcode-auto-multicandidate-qa-gate]]` é assunto
diferente — este é o POC de fine-tuning, não confundir). Script da Lia preservado intacto em
`.claude/tmp/dataset-finetuning/build_dataset.py`; script novo `.claude/tmp/dataset-finetuning/filter_dataset.py`
refaz o matching tcl<->xsl **case-insensitive** e aplica o filtro de versão final.

**Bug de case corrigido:** matching original de `build_dataset.py` é case-sensitive e perdia 4 pares
CTe (`CTe001_retConsSitCte`/`CTe004_retConsSitCte`/`CTe200_retConsSitCte` em 1.04c/2.00/2.00a — tcl com
"Cte" minúsculo, xsl com "CTe" maiúsculo). Fix confirmado: os 4 pares recuperados estão **todos** em
versões descartadas pelo filtro (1.04c/2.00/2.00a não são a versão final de CT-e) — recuperação líquida
no dataset final = **0 pares**. Documentado, não é um problema real para a Fase 2.

**Regra de filtro de versão aplicada** (decisão do usuário, não reinterpretada):
- NFe: só 4.00 + 4.000 (mesma versão semântica; bug de nome de pasta duplicado no corpus —
  `NFe/4.00/` tem 16 tcl, `NFe/4.000/` tem 15 tcl, mas o conteúdo dos arquivos **não é idêntico**:
  `4.000` tem campos extras de schema mais recente, ex. ICMS mono (`vICMSMono_ICMS` etc.), CPF em
  endereço, `gCred`. Ou seja, não é duplicata exata — são duas revisões do mesmo doctype 4.00, ambas
  mantidas por serem a mesma versão nominal).
- CTe: só 4.00. MDFe: só 3.00. NFSe: descartado por completo (layout G2KA de outro produto).

**Contagem final** (`dataset_pairs_filtered.jsonl`, 39 pares):
- NFe: 31 (16 de `4.00` + 15 de `4.000`)
- MDFe: 6 (só existe `3.00` no corpus, nada descartado)
- CTe: 2 (`CTe400_EnvioCTe_NeogridToSefaz` + `CTe400_RetEnvCTe_SefazToNeogrid`) — **amostra muito fina**,
  ver bloqueio abaixo.

**Amostragem manual (11 pares lidos em par tcl+xsl completo):** 2 CTe, 3 MDFe, 6 NFe. Todos os 11
vereditos = **OK** (correlação semântica real: campos do `.tcl` aparecem de fato mapeados no `.xsl`
correspondente, direção Neogrid<->Sefaz condizente com o nome do arquivo). Único ponto sinalizado
(não bloqueante): `NFe008a_CancNFe_NeoGridToSefaz.xsl` em `NFe/4.00/` hardcoda
`<xsl:attribute name="versao">2.00</xsl:attribute>` mesmo estando na pasta 4.00 — schema de campos do
cancelamento não mudou entre 2.00 e 4.00 (por isso os campos batem), mas o valor de versão de saída
está desatualizado/inconsistente com a pasta. Reportar para `@lp-parser-llm`/`@lp-backend-dev` para
decidir se corrige antes do fine-tuning ou se é aceitável como está no corpus real.

**Veredito QA:** dataset **confiável** para avançar à Fase 2 (RAG spike), COM UMA RESSALVA: a amostra
de CT-e é de só 2 pares — não dá pra generalizar confiança de CT-e com essa base; recomendo tratar
CT-e como "baixa confiança / poucos exemplos" explicitamente na Fase 2, e não bloquear NFe/MDFe por
causa disso. NFSe não faz mais parte do escopo (descartado por decisão do usuário, não é falha de dataset).

Arquivos gerados: `dataset_pairs_filtered.jsonl`, `filter_report.json`,
`dataset_pairs_all_case_insensitive.jsonl` (intermediário, 159 pares pré-filtro), todos em
`.claude/tmp/dataset-finetuning/`.
