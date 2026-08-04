---
name: idoc-textpositional-overload
description: LayoutType "TextPositional" cobre dois formatos físicos incompatíveis (MQ stream contínuo vs IDOC record-oriented); o discriminador real é WithBreakLines, que o parser nunca lê — resultado é corrupção silenciosa no IDOC.
metadata:
  type: project
---

`LayoutType=TextPositional` é um tipo **sobrecarregado**: cobre tanto o MQSeries (stream
contínuo de 600 chars, sem quebra de linha, campo `Sequencia` de 6 chars por registro) quanto
o IDOC SAP (registro por linha com LF, largura variável por segmento, sem `Sequencia`).
O discriminador que separa os dois **já existe no XML do layout** — `<WithBreakLines>` — e é
lido para a entidade (`Services/Generation/Implementations/XmlLayoutLoader.cs`), mas
**nenhum caminho de parsing o consulta**. O parser compensa com heurísticas de string
espalhadas (`text.StartsWith("EDI_DC40")`, `Name.StartsWith("LINHA")`, `InitialValue.StartsWith("ZRSDM_")`).

Convenção do layout IDOC (validada em 136/139 segmentos): `len(InitialValue) + len(campo "content") == 63`,
que é exatamente o header EDI_DD40 (SEGNAM 30 + MANDT 3 + DOCNUM 16 + SEGNUM 6 + PSGNUM 6 + HLEVEL 2).
Ou seja, o mapeador modela o IDOC corretamente; quem erra é o runtime.

**Why:** teste do Elson em 2026-08-03 (mapeador `LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe`, doc IDOC
da Marelli). Resultado: 55/55 linhas identificadas, zero erro reportado, **100% dos campos com
valor errado** — `CUF='47'` em vez de `'35'`, `MOD='00'` em vez de `'55'`. É corrupção silenciosa,
o pior modo de falha: `Success=true` com dado fiscal inválido. A causa é a regra `offset += 6`
(sequencial do MQ) aplicada a um formato que não tem esse campo.

Achado separado, mesmo teste: `ParseController` **nunca checa `result.Success`**. Quando o parse
do layout falha, `ParseAsync` devolve `Success=false` + a mensagem real do erro, e o controller
segue direto para `ReestruturarLayout(null)` → `NullReferenceException` → HTTP 500 genérico
("Object reference not set..."). A mensagem que diria a causa é destruída. Verificado com harness:
layout íntegro → 200 com 263 campos; layout não-parseável → 500 opaco. Efeito no front: `parseResult`
fica null e a tela mostra só o placeholder — indistinguível de "não processei nada ainda".

⚠️ **Armadilha ao aplicar isso (verificada em 2026-08-03):** o `WithBreakLines` **não chega** ao
pipeline de parse. O loader do fluxo Parsing nunca lê o elemento do XML (só o loader do fluxo
Generation lê), e `LayoutNormalizer.ReestruturarLayout` + o `flattenedLayout` do `ParseController`
criam `new Layout` sem copiar o campo. Efeito: `layout.WithBreakLines` é **sempre `false`** no
parse, independente do XML — ligar o splitter nele sem fechar essa cadeia "corrige" nada e dá falsa
sensação de correção. Some-se a isso que o helper de bool colapsa ausente/`false`/malformado no
mesmo `false`, então "ausente" e "false explícito" são indistinguíveis sem um tri-estado.

**How to apply:** ao desenhar qualquer coisa que dependa de "tipo de layout", não confie em
`LayoutType` — ele não distingue os dois formatos. Trate `WithBreakLines` como o discriminador
canônico e prefira promovê-lo a decisão explícita no domínio a adicionar mais uma heurística de
string. Vale também para os gates `detectedType == "mqseries"` (que hoje excluem o IDOC do
pathway de transformação low-code) e para qualquer feature de IA/diagnóstico que assuma
largura fixa de linha. Decisão formalizada em `docs/architecture/adr-001-discriminador-formato-posicional.md`
(+ spec de execução `spec-fase3-fase4-gate-transformacao-e-dataset.md`) — conferir se já foi
implementada antes de recomendar de novo. Relacionado: [[transformation-pathway-duplication]],
[[ia-fiscal-diagnosis-vision]].
