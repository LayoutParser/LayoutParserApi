---
name: rag-fewshot-b4
description: "B4/P1 RAG few-shot: o que o corpus layoutparser realmente contém, decisões do FewShotIndex e gotchas (interpretador parcial em regras difíceis, Ollama instável)"
metadata:
  type: project
---

B4 (P1) entregue em 2026-07-15: `ai/XslSynth/Synthesis/FewShotIndex.cs` + few-shot opt-in no `DslRuleTranslator` + flags `--rag <pasta>` / `--rag-stats` no Program.cs do XslSynth.

**Fatos do corpus** (`…/LayoutParserApi/.claude/tmp/servidor/layoutparser/`, somente-leitura):
- **NÃO há pares DSL→XSLT em claro.** As regras DSL do Sysmiddle vivem no `globalfolder/exportContext.data` (119 MB, criptografado) — inutilizável sem decrypt.
- O utilizável: `Examples/xsl/` = **191 XSLs reais de produção** por tipo×versão (NFe 2.06b/2.06c/3.10/4.00, CTe, MDFe, NFSe) — 116 com `xsl:otherwise` (else), 69 com `test` composto com `and`. Indexados como few-shot de ESTILO. `Examples/tcl/` espelha (parsers, não transform).
- DSL real vem do mapeador descriptografado `…/.claude/tmp/export/MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.decrypted.xml` (98 regras; 96 `&&` — escapado como `&amp;&amp;` no XML! — e 141 `else`).
- "G2KA" só aparece em nomes de XSL NFSe e em logs — o "corpus G2KA" dos docs é, na prática, `Examples/xsl`+`tcl`.

**Gotcha de correção (importante):** o `DslBlockInterpreter.Interpret` em regra DIFÍCIL (else/&&/aninhada) consome só os blocos que reconhece e o `DirectEmit` pega `T.x=…;` de dentro de branches → emissões INCONDICIONAIS erradas com cara de verificadas. No FewShotIndex só emparelho interpretador↔DSL para regras fáceis. **O fluxo RunRealAsync tem o mesmo risco** (interpreter primeiro, LLM nunca vê a regra se houver ≥1 emissão) — follow-up aberto.

**Ollama nesta máquina:** respondeu `/api/tags` (qwen2.5-coder:7b) e minutos depois recusou conexão (nada ouvindo em 11434) — serviço instável/parado por outra sessão. Comparação com/sem few-shot ficou pendente; o código já suporta (`--rag-stats --ollama`).

**How to apply:** para melhorar a tradução das regras difíceis, o caminho é estilo-XSLT real + DSL análoga no prompt (não há gold pairs); rodar a comparação Ollama quando o serviço estiver de pé.
