---
name: layout-tree-target-roots-vazio-433
description: causa raiz de target.roots vazio no GET .../layout-tree (issue #433) — filtro TextPositional do warmup Redis vazava pro lookup por GUID
metadata:
  type: project
---

`Services/Database/LayoutDatabaseService.cs:158` — `SearchLayoutsFromDatabase` descarta
silenciosamente qualquer layout que não seja `TextPositional` (`IsTextPositionalLayout`). Esse
filtro foi desenhado só pro warmup do cache Redis (`RefreshCacheFromDatabaseAsync` — só layouts de
ENTRADA/posicionais servem pro parse), mas `CachedLayoutService.GetLayoutByGuidAsync`
(`Services/Database/CachedLayoutService.cs:136`) reaproveitava a MESMA busca no fallback de banco,
pra QUALQUER GUID.

`LayoutTreeService.ResolveSideAsync` (issue #425) usa `GetLayoutByGuidAsync` pros dois lados —
source (sempre `TextPositional`, layout de entrada) E target (sempre `XmlLayoutVO`, layout de
saída). Resultado: `source.roots` sempre populava, `target.roots` sempre vinha `[]`, sem log de
erro nem sinalização em `Limitations` — o filtro engolia o layout antes mesmo de chegar em
`GuidXPathCatalog.BuildTree`.

**Fix (branch `fix/layout-tree-target-roots-433`):** `LayoutSearchRequest.IncludeAllLayoutTypes`
(default `false`, preserva o warmup) — `CachedLayoutService` seta `true` no fallback de GUID.
Teste novo `tests/.../Database/CachedLayoutServiceGuidLookupTests.cs` reproduz com fake de
`ILayoutDatabaseService` que aplica a mesma regra do filtro real.

**Não validado contra dado real** (mapper `MAP_f1a6453f-...` da issue) — só análise de código +
teste sintético. `LayoutTreeServiceTests.cs` (existente, issue #425/#430) já usava
`FakeCachedLayoutService` implementando `ICachedLayoutService` diretamente, então nunca exercitava
`CachedLayoutService`/`LayoutDatabaseService` de verdade — por isso o bug não foi pego antes.

**Lição:** qualquer bug futuro em busca por layout/GUID que "funciona pra um tipo mas não outro"
— suspeitar primeiro do filtro `TextPositional` em `LayoutDatabaseService`, que existe em pelo
menos 2 pontos de entrada (`SearchLayoutsAsync` genérico e o fallback de `GetLayoutByGuidAsync`).
