---
name: issue-219-layouttype-numerico-normalizer
description: Fix da issue #219 (gate FIAT "Tipo de layout não suportado: 2") — LayoutDatabaseService.IsTextPositionalLayout já resolve LayoutType via XML mas descarta o valor; padrão reaproveitável.
metadata:
  type: project
---

Issue #219 (2026-09-03): `POST /api/AutoTransformation/generate-for-layout` recusava o layout
FIAT `LAY_TXT_MQSERIES_ENVNFE_4.00_NFe` (GUID `ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c`) com
`"Tipo de layout não suportado: 2"`.

**Causa raiz:** `tbLayout.[LayoutType]` (coluna SQL crua) pode conter código numérico legado do
Sysmiddle ("2") em vez do texto esperado ("TextPositional"/"XML"). O
`AutoTransformationGeneratorService.ProcessLayoutAsync` comparava esse valor cru literalmente.

**Achado não-óbvio, vale lembrar:** `LayoutDatabaseService.IsTextPositionalLayout` **já resolve
exatamente essa divergência**, mas descarta o resultado — ele lê `/LayoutVO/LayoutType` do XML
descriptografado do próprio layout (fonte comprovadamente autoritativa: decide hoje se o layout
entra no catálogo Redis) só para devolver um `bool`, sem propagar o valor string real adiante.
Ou seja, o sistema já "sabe" resolver esse tipo de divergência SQL-vs-XML em um lugar, mas o
resultado nunca chega em quem mais precisa dele (o gate de geração).

**Fix aplicado** (PR #295, branch `fix/layout-type-numerico-issue-219`): extraí
`Services/XmlAnalysis/LayoutTypeNormalizer.cs` (classe estática, sem DI, testável isolada) com
`ResolveEffectiveLayoutType`, ordem de resolução: (1) valor cru já reconhecido → usa; (2) lê
`/LayoutVO/LayoutType` do XML descriptografado (mesma lógica de busca de
`IsTextPositionalLayout` — root==LayoutVO / LayoutVO filho / LayoutType direto no root) → usa e
loga Warning de divergência; (3) fallback heurístico só para `"2"` → `TextPositional`, **não
confirmado pelo dono**, só entra se nem o XML estiver legível, Warning explícito pedindo
confirmação; (4) senão devolve valor cru (cai no warning "não suportado" já existente).

Nenhum enum/mapa de códigos numéricos Sysmiddle→tipo foi encontrado documentado em lugar nenhum
do repo (verificado: `LayoutRecord.cs`, `Layout.cs`, `LayoutDatabaseService.cs`, ADR-001,
`PositionalFormat.cs`) — a heurística "2"→TextPositional é baseada só na evidência real deste
caso (layout é de fato MQSeries/TextPositional), não em documentação. **Se aparecer outro código
numérico** (ex. "1", "3") em outra issue, resolver do mesmo jeito: preferir a leitura do XML
(passo 2) antes de inventar mapeamento novo no passo 3.

Testes: `tests/.../LayoutTypeNormalizerTests.cs`, 7 casos. `dotnet test`: 584/584 verdes.

Se este padrão de "coluna SQL crua diverge do XML autoritativo" aparecer de novo em outro
campo (não só LayoutType), vale considerar propagar o valor resolvido por
`IsTextPositionalLayout` (ou equivalente) direto no `LayoutRecord`, em vez de recalcular em cada
consumidor — não fiz isso agora para manter o fix mínimo e escopado à issue #219.
