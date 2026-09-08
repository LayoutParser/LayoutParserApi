---
name: reavaliacao-ia-fiscal-especializada-2026-09-08
description: Reavaliação pedida pelo dono — fine-tuning de sintaxe deu certo (QLoRA CPU-only real), mas julgamento fiscal (CFOP/ST) é dataset diferente que não existe; decisão híbrida assimétrica, não fine-tuning geral
metadata:
  type: project
---

Em 2026-09-08 o dono perguntou se, dado que o QLoRA de sintaxe (TXT→TCL/XSLT) funcionou de
verdade em CPU-only (VM `172.25.32.5`, 4 vCPU/15GB RAM, sem GPU — mais fraca que
`BRNDDAPPBLD01`), faz sentido também treinar julgamento de regra fiscal (CFOP, substituição
tributária) em vez de só RAG. ADR completo:
[`docs/architecture/adr-reavaliacao-ia-fiscal-especializada-2026-09-08.md`](../../../docs/architecture/adr-reavaliacao-ia-fiscal-especializada-2026-09-08.md).

**Decisão: híbrido assimétrico, não "sim" nem "não" uniforme.**
- CFOP × tipo de operação = lookup de tabela finita → **RAG**, nunca fine-tuning (natureza do
  problema, não hardware — reafirma [[ia-fiscal-diagnosis-vision]]).
- ST/regras fiscais mais fuzzy → RAG primeiro; fine-tuning só quando existir dataset rotulado
  real de volume suficiente (não hoje — bloqueio agora é DADO, não hardware).
- Sintaxe DSL/TCL/XSLT → já resolvido, fora do escopo desta reavaliação, ver [[fine-tuning-nichado-ollama-2026-09-02]].

**Achado que sustenta a decisão:** dataset de sintaxe (`sysmiddle-dsl-dataset-2026-09-02.jsonl`,
6044 exemplos) é 100% `(layout_input, xsd_destino, regra_dsl)` — zero exemplos de
`(documento_fiscal, regra_aplicável, veredito_correto)`. Busca por "CFOP" no repo inteiro só
retorna a menção na visão de arquitetura de julho — continua greenfield, RAG nunca foi
implementado. Confundir "hardware deixou de bloquear" com "dataset também deixou de faltar" seria
o erro a evitar aqui.

**Risco de memorização mais forte que no caso já resolvido:** corpus real pequeno
([[backfill-catalogo-nao-viavel-2026-09-08]], 4/54 pares) + ausência de verificador determinístico
tipo XSD para veredito fiscal (diferente de sintaxe, onde XSD pega erro objetivamente) = risco
real de o modelo decorar exemplo em vez de generalizar regra, mascarado como "acertou".

**Plano recomendado se avançar:** Fase RAG de CFOP primeiro (greenfield, sem dependência de dado
real) → captura incremental de veredito humano curado como subproduto do endpoint de correção
guiada ([[contrato-correcao-guiada-humano-2026-09-08]]) → gate de volume explícito antes de
reabrir fine-tuning fiscal, não número arbitrário fixado agora.

**How to apply:** se alguém pedir "treinar CFOP" ou "fine-tuning fiscal" de novo, apontar pra
este ADR antes de recomendar — a resposta não é "não, nunca" nem "sim, já que sintaxe funcionou",
é "depende de qual sub-problema e do dataset que ainda não existe".
