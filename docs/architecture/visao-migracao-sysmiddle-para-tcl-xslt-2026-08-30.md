# Visão estratégica — migração do formato Sysmiddle para TCL/XSL-XSLT (2026-08-30)

> **PT-BR.** Autoria: `@lp-architect`. Decisão confirmada pelo dono nesta sessão. Não implementa
> nada — consolida a visão pra orientar o roadmap tático já em execução (fine-tuning) e as
> decisões técnicas relacionadas (mecanismo Sysmiddle decifrado, estudo de migração Linux).

## 1. Resumo executivo

Decisão do dono (textual): *"vamos migrar esse XML (de layout, transformação, regras) da
Sysmiddle para o TCL (quando TXT) e XSL/XSLT — os TCL e XSL/XSLT que anexei como exemplos são de
criações humanas que já estão em produção, então podemos usar eles como base (padrão)."*

Leitura arquitetural: o objetivo de médio prazo deixa de ser "gerar transformação com IA como
capacidade adicional" e passa a ser **eliminar a dependência do formato proprietário de
layout/mapeamento/regras da Sysmiddle** (o `RuleInterpretor` line-based, mecanismo real decifrado
nesta mesma sessão via ILSpy). No lugar:

- **Pathway TXT → TCL → XSL/XSLT → XML**: TCL vira o formato intermediário de *layout posicional*
  (substitui o XML de layout Sysmiddle).
- **Pathway XML → XSL/XSLT → XML**: XSLT puro substitui o XML de regra/transformação Sysmiddle
  diretamente, sem intermediário.

Os **259 pares `.tcl`/`.xsl` reais** em `Examples\{tcl,xsl}\` — criação humana, já em produção —
não são dataset de treino descartável: são o **padrão-ouro**. Definem o "certo" que qualquer
mecanismo (modelo fine-tuned hoje, pipeline determinístico complementar amanhã) precisa aprender
a replicar. Isso não é uma ideia nova desta sessão — o POC de fine-tuning já trazia "eliminar
Sysmiddle" como objetivo declarado (ver `finetuning-poc-fase1-dataset.md` /
`finetuning-poc-fase2-filtro-v2-e-rag-spike.md`, memória de `@lp-parser-llm`); o que muda aqui é
que o dono **confirma e formaliza** essa direção como decisão estratégica, não mais hipótese de
POC, e amarra explicitamente aos exemplos humanos como gabarito de aceitação.

## 2. Estado atual — as peças do quebra-cabeça

| Peça | Status | Onde |
|------|--------|------|
| **De onde**: mecanismo Sysmiddle decifrado | ✅ Fechado — `RuleInterpretor` é interpretador proprietário line-based (sentinelas `%beginRuleContent;`/`%endRuleContent;`, `begin/end`, operador `=`/`!=` sobre string, dispatcher fechado de funções). Não é Roslyn/CodeDom. | `docs/architecture/decisao-dsl-mapper-sysmiddle-2026-08-21.md` |
| **Gabarito**: dataset humano real | ✅ Localizado — 259 pares `.tcl`/`.xsl` em produção, `Examples\{tcl,xsl}\`; 54 pares filtrados/QA'd via `filter_dataset_v2.py` (CTe 15, MDFe 6, NFe 33) | `.claude/agent-memory/lp-parser-llm/finetuning-poc-fase2-filtro-v2-e-rag-spike.md` |
| **Como aprender**: pipeline de fine-tuning | 🔄 Em execução — Fase 1 (smoke-tests #1-#4), treino de 3 épocas rodando agora na VM sobre 1 par real (ver §5) | `docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md` *(referenciado pela tarefa; não localizado neste worktree — provavelmente em worktree irmão `agent-aef441626a11b2eb9`, confirmar caminho antes de linkar em README)* |
| **Onde plugar**: integração em produção | ✅ Já existe — `RepairOrchestrator` (loop gerar→validar→corrigir) em produção desde PR #211 | Código do repo, não revisado nesta sessão |

## 3. Dependência com a iniciativa de migração Linux (sinalizada, não resolvida)

`docs/architecture/estudo-migracao-linux-ollama-2026-08-21.md` concluiu que o `LowCodeRunner`
(`.exe` Sysmiddle) precisa continuar em Windows por dependência nativa x86/net481 — esse é hoje
o boundary que trava a API inteira em Windows.

Essas são **duas iniciativas distintas com escopo diferente**, mas não independentes:

- Migração de **infra** (Linux): resolve *onde* a Sysmiddle roda, sem tocar no *formato*.
- Migração de **formato** (esta visão): resolve *o que* é interpretado — se bem-sucedida a ponto
  de eliminar a necessidade de invocar o `.exe` Sysmiddle em runtime (porque o pathway
  TCL/XSLT já cobre o que hoje passa pelo `RuleInterpretor`), **o boundary Windows do estudo de
  migração Linux deixa de existir** — não porque o estudo estava errado, mas porque a premissa
  que o motivou (precisar do `.exe`) some.

Não decidir isso agora. Só registrar: qualquer replanejamento da migração Linux deve checar o
progresso desta visão antes de assumir que o boundary Windows é permanente.

## 4. Riscos e incertezas honestas

1. **Generalização não comprovada.** O treino atual usa **1 par real** (Fase 1/smoke-test). Um
   modelo fine-tuned nesse par não garante nada sobre os outros 258 — é validação de mecânica
   (o ciclo de treino roda, a loss cai), não de capacidade.
2. **Custo de escala não resolvido.** A extrapolação para o dataset completo com chunking total
   projetava ~56,6h de treino (registrado nos smoke-tests #2/#3) — sem solução fechada de como
   reduzir isso a um ciclo viável (batching mais agressivo? menos épocas? amostragem estratificada
   por tipo de documento em vez do dataset completo?). Trade-off ainda em aberto.
2b. Hardware é CPU-only (`BRNDDAPPBLD01`, sem GPU — ver memória de `@lp-architect`,
   `finetuning-small-model-poc.md`), o que é a causa raiz do custo alto — não é só questão de
   configuração de treino.
3. **Framework de avaliação formal não existe.** Hoje a validação é amostragem manual (QA leu 4
   pares e confirmou correspondência semântica campo↔`xsl:value-of`). Falta um diff estruturado
   XML/XSLT (comparação de árvore, não de texto) que sirva de gate objetivo antes de qualquer
   candidato ir para o `RepairOrchestrator` em produção.
4. **TCL como *output* de IA é capacidade nova, não só reaproveitamento.** O gap identificado em
   sessão anterior (geração de TCL via IA "não existe hoje" — `gap-real-ollama-geracao-tcl-xsl-
   2026-08-21.md`, referenciado pela tarefa mas não localizado neste worktree) deixa de ser
   pergunta em aberto: o dono confirmou nesta sessão que gerar TCL via IA **é** objetivo real do
   pathway TXT. Ou seja, o escopo de "gap" descrito lá vira escopo de trabalho confirmado, não
   mais dúvida de alinhamento — quem retomar aquele documento deve atualizá-lo para refletir isso.

## 5. Status do treino em andamento (snapshot, não aguardado até o fim)

VM `172.25.32.5`, processo `smoke_train_single_pair.py` (PID 19807), 3 épocas sobre
`single_pair_chunked.jsonl` (1 par real, chunk_size=2048, batch=57).

- **Passo 159/171** (93%), época 2.79 de 3.
- **Tempo decorrido:** ~5h18min desde o início (13:20).
- **Loss:** oscilando entre ~0.44 e ~0.57 nos últimos 15 passos, sem tendência clara de queda
  adicional (já estava nessa faixa desde bem antes do passo 145) — comportamento esperado perto
  do fim do treino num único par (risco de overfit no par específico, coerente com o objetivo do
  smoke-test, que é validar mecânica, não generalização).
- **Processo vivo**, ritmo estável (~118-120s/passo). Restam 12 passos.
- **ETA:** ~24 minutos para completar as 3 épocas (171/171).

## 6. Próximos passos sugeridos (opções, sem decidir)

Quando o treino de 3 épocas terminar:

- **A. Expandir para os 54 pares filtrados** (não os 259 brutos) antes de qualquer decisão de
  escala total — trade-off: valida generalização real vs. ainda é sub-amostra; custo intermediário
  entre o smoke-test de 1 par e o dataset completo.
- **B. Construir o framework de avaliação estruturado primeiro**, antes de expandir o dataset de
  treino — trade-off: atrasa a próxima rodada de treino, mas evita treinar em escala sem conseguir
  medir se generalizou.
- **C. Paralelizar** A e B (avaliação em cima do modelo do par único enquanto se prepara o dataset
  de 54) — trade-off: mais rápido em wall-clock, mas divide atenção do `@lp-parser-llm`.

Recomendação de sequenciamento fica para quando os smoke-tests atuais fecharem — não travar essa
decisão nesta visão.
