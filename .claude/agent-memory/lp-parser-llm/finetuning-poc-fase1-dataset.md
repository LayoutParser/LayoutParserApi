---
name: finetuning-poc-fase1-dataset
description: Estrutura real de Examples/ para o POC de fine-tuning LoRA e dataset extraído (155 pares tcl->xsl reais)
metadata:
  type: project
---

Fase 1 do POC de fine-tuning (LoRA, modelo pequeno em CPU, candidatos Qwen2.5-Coder-1.5B /
DeepSeek-Coder-1.3B — não decidido) concluída em 2026-07-28. Baseline de produção continua
sendo `deepseek-coder:6.7b` via Ollama (`appsettings.json` linha 23) — sem mudança.

**Estrutura real de `Examples/`** (`.claude/tmp/servidor/layoutparser/Examples/`):
- Pastas `LAY_*`/`MAP_*` (ex.: `LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe`, `MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE`)
  contêm só **amostras brutas de entrada** (`.txt`/`.mq_series`) + `layout_learned.json` (saída do
  aprendizado de layout). **Não têm XML/tcl/xsl de saída pareado** — servem só pra detecção/aprendizado
  de layout, não pra treinar geração de transformação.
- `Examples/tcl/<DocType>/<Versao>/*.tcl` (159 arquivos) — apesar do nome ".tcl" **não é script Tcl**,
  é um XML de definição de MAP (`<MAP><LINE identifier=...><FIELD/><CHILD/>`) descrevendo a forma/campos
  do XML intermediário (ROOT) que sai do parser posicional.
- `Examples/xsl/<DocType>/<Versao>/*.xsl` (191 arquivos) — XSLT real de produção que transforma esse
  XML intermediário no XML final (SEFAZ/NeoGrid/G2KA/TSS conforme o par de sistemas no nome do arquivo).
- **Correlação real: nome de arquivo (stem) idêntico entre `tcl/` e `xsl/` dentro do MESMO `DocType/Versao`.**
  Usar só o stem (ignorando doctype/versão) COLIDE — o mesmo nome de arquivo se repete em versões
  diferentes (ex.: `CTe200_CancCTe_NeogridToSefaz.tcl` existe em `CTe/2.00` E `CTe/2.00a` com conteúdo
  DIFERENTE). Gotcha real encontrado: primeira versão do script colapsou 159→113 tcl e 191→139 xsl por
  chavear só pelo stem; a chave correta é `doctype/versao/stem`.
- DocTypes cobertos: NFe (75 tcl), CTe (63 tcl — nota: 4 pares sem par foram descontados, 59 pares reais),
  MDFe (12), NFSe (9). CTe/MDFe têm várias versões (1.04c, 2.00, 2.00a, 3.00, 4.00 etc.), cada uma com
  seu próprio conjunto completo de arquivos — não é incremental entre versões.

**Formato de dataset escolhido para Fase 1: par completo (tcl inteiro, xsl inteiro)**, não o formato
"structured output" (JSON de slots {sourceField, targetXPath, transformFunction}) que a Aria desenhou
para a visão de longo prazo. Justificativa: a base real não contém pares documento-de-entrada→XML-final
já anotados por campo — o que existe é a REGRA (esquema de campos + XSLT) em si, então o par
natural e sem invenção de anotação é (schema/MAP .tcl = contexto de campos disponíveis) → (.xsl = regra
alvo). Extração de slots {xpath origem, atributo/elemento alvo} foi feita via regex best-effort
(`xsl:value-of`/`xsl:attribute`) e incluída como campo extra (`extracted_slots`) no mesmo JSONL, pronta
pra quem quiser tentar o formato estruturado na Fase 2 sem re-processar os arquivos.

**Resultado real:** 155 pares (tcl+xsl) extraídos de 159 tcl / 191 xsl (36 xsl sem tcl correspondente —
variantes tipo `_v2`, `_XML`, `modficado`; 4 tcl sem xsl). Script: `.claude/tmp/dataset-finetuning/build_dataset.py`
(Python stdlib, sem dependências). Saída: `.claude/tmp/dataset-finetuning/dataset_pairs.jsonl` (155 linhas)
e `unmatched_report.json` (relatório dos não-casados).

**Riscos/limitações identificados para a Fase 2/3 (fine-tuning em si):**
1. Volume pequeno (155 exemplos) para um modelo genérico — recomendável tratar como fine-tuning de
   ESTILO/FORMATO (aprender a "sotaque" do XSLT de produção), não como fonte de todo conhecimento de
   mapeamento; o loop RAG→validar→corrigir continua sendo o mecanismo principal de correção semântica.
2. Tamanho MUITO desigual dos arquivos: `.xsl` varia de 454 a 153.438 caracteres (média ~16.6k). Os
   maiores estouram fácil a janela de contexto de um modelo 1.5B/1.3B em CPU — vai precisar de
   truncamento/chunking ou filtro por tamanho máximo antes do fine-tuning real.
3. Desbalanceamento por doctype (NFe domina com 75/155) — risco de overfit no "sotaque" de NFe e
   generalização fraca pra CTe/MDFe/NFSe.
4. Correlação por nome de arquivo é uma NOMEAÇÃO DE PRODUÇÃO, não uma anotação garantida por conteúdo —
   não validei semanticamente se todo par tcl/xsl casado é de fato o par certo (ex.: nunca vi um teste
   automatizado rodando o xsl contra uma instância do tcl). Antes de confiar 100% no dataset, vale uma
   amostragem manual (Quinn/@lp-qa) conferindo uns 10-15 pares.
5. Os 36 xsl "órfãos" (sem tcl) são variantes reais em produção (`_v2`, `_XML`, `_modficado`) — bom sinal
   de que já existe divergência de regra por variante de sistema, o que pode ser mais insumo (não
   incluído nesta primeira leva porque não tem contexto de campo/schema pareado).

Ver também [[rag-fewshot-b4]] (corpus de 191 XSLs reais já usado como estilo no RAG few-shot) — este
dataset da Fase 1 é uma extração mais estruturada/pareada do MESMO corpus físico, agora com o
schema tcl correlacionado.
