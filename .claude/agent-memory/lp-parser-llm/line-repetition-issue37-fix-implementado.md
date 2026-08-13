---
name: line-repetition-issue37-fix-implementado
description: "Issue #37 corrigida - IsPositionalGroupRepetition agora agrega LINHA081/infCpl; causa raiz real era deserialização (flag nunca lida do XML), não só falta de lógica de agregação"
metadata:
  type: project
---

**Commit real da correção:** `f1f4494` na branch `fix/agregacao-infcpl` (base `origin/develop`,
3a1e28b). **NÃO** está em `fix/remove-pathway1-transformacao` — ver "Gotcha de working tree
compartilhada" abaixo antes de mexer em git nesta sessão/branch.

**Causa raiz era DUPLA, não só a que a Aria desenhou.** O plano de `docs/architecture/fix-agregacao-repeticao-linha-infcpl.md`
assumia que bastava adicionar o passo de agregação. Na prática, `ParseLayoutFromXDocument` /
`ParseLineElementWithHierarchy` (`Services/Implementations/LayoutParserService .cs:1236`) **nunca
lia `IsPositionalGroupRepetition` do XML** — o construtor de `LineElement` populava
`MinimalOccurrence`/`MaximumOccurrence`/etc. mas omitia essa flag, que ficava sempre `false` por
default do bool. Rodei o teste de regressão ANTES de perceber isso e o count de campos não mudou
(702→702) mesmo com a agregação implementada — só ao inspecionar o dump percebi que `LINHA081` não
tinha nenhum `Occurrence=0`. Sem esse fix de deserialização, a agregação nunca dispara para
NENHUM layout, mesmo que o XML declare a flag corretamente.

**Validação contra gabarito real confirmou E corrigiu a hipótese da Aria em 1 ponto:**
concatenação SEM separador estava certa, mas "sem trim adicional" estava errado. Cada fragmento de
`InformacoesParaEDI` (500 chars, `AlignmentType=Left`) só tem `TrimEnd()` aplicado por
`ApplyAlignment` — o padding de ALINHAMENTO DE TEXTO dentro do campo (20 espaços à esquerda antes do
texto real, ex.: `'                    Solicitante: ...'`) permanece. Concatenar bruto produzia
`'...Costa// Pamela Guedes                    PIS ST...'` (espaços no meio) contra o esperado
`'...Guedes PIS ST...'` sem gap. Fix: `f.Value?.Trim()` em cada fragmento antes de concatenar,
só dentro de `AggregatePositionalGroupRepetitions` — não mexe no `ParsedField.Value` original
(usado por `ValidateLineOccurrences` e por qualquer outro consumidor dos fragmentos 1..N).

**Gabarito usado:** par `.claude/tmp/26072026/LAY_TXT_MQSERIES_ENVNFE_4.00_NFe.xml` +
`.claude/tmp/26072026/QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series.txt` (já usado por
`PositionalFormatRegressionTests.MqSeries_de_controle_mantem_saida_identica_ao_baseline`) — confirmei
via `diff` que é BYTE A BYTE o mesmo TXT do par em `.claude/tmp/exemplos/txt input/...` +
`.claude/tmp/exemplos/xml output/...-env.xml` (o XML esperado com `<infCpl>` real só existe nessa
segunda pasta). `infCpl` esperado: `"Solicitante: Ana B Costa// Pamela GuedesPIS ST - Valor
0,00COFINS ST - Valor 0,00"` — bate exato com o valor agregado após o fix de trim.

**Design final (fiel ao plano da Aria, exceto o trim):** aditivo, `Occurrence=0` marca o valor
agregado, `ValidateLineOccurrences` roda ANTES da agregação (não depois — inverti a ordem do texto
do plano por segurança extra, mas o resultado é o mesmo: a contagem por `GroupBy(Occurrence)` nunca
vê o `Occurrence=0`). Só há 1 `LineElement` com a flag `true` em todo o layout auditado (LINHA081) —
não achei segunda instância nesta sessão, então não parametrizei separador (fica hardcoded "sem
separador com trim por fragmento" até aparecer um segundo caso real, como a Aria sugeriu).

**Teste de regressão:** `tests/LayoutParserApi.Tests/Parsing/PositionalFormatRegressionTests.cs` —
baseline do hash/count do teste de controle MQSeries atualizado (702→704 campos, hash recapturado) +
novo teste `LINHA081_agrega_infCpl_igual_ao_xml_esperado_do_gabarito_real` que trava o valor
byte a byte contra o XML esperado real (não hipótese sintética) e confere que os 4 fragmentos físicos
(Occurrence 1..4) continuam intactos. `dotnet test` completo: 296/296 verde.

**Gotcha de working tree compartilhada (aconteceu nesta sessão, evitar repetir):** o repo tem
múltiplos agentes (`@lp-backend-dev` em `fix/remove-pathway1-transformacao`, outros) operando no
MESMO diretório de trabalho ao mesmo tempo. Rodei `git checkout -b fix/agregacao-infcpl
origin/develop` no início, mas entre minhas edições e o `git commit`, outro agente trocou a branch
ativa do working tree de volta para `fix/remove-pathway1-transformacao` (sem eu perceber) — meu
commit `b6b1df0` foi parar na branch ERRADA, em cima do trabalho dele, que já tinha avançado mais 2
commits por cima antes que eu notasse (`git log` mostrou `resolvendo conflito` e `atualizando
memória` entre o meu commit e o HEAD atual). Como reset/force não eram seguros (destruiria os
commits dele), resolvi com: (1) `git worktree add` num diretório separado apontando pra
`fix/agregacao-infcpl`, `git cherry-pick b6b1df0` lá — isso ISOLA a operação do working tree
compartilhado; (2) `git revert --no-edit b6b1df0` (não reset!) direto no working tree principal, que
é seguro em cima de qualquer commit novo por cima porque revert não reescreve histórico, só soma um
commit que desfaz o meu. **Lição:** em qualquer sessão futura com branches compartilhadas entre
agentes, considerar `git worktree add` DESDE O INÍCIO em vez de `git checkout -b` no working tree
principal — evita esse tipo de corrida inteiramente.
