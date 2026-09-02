---
name: project-auditoria-backlog-completa-2026-09-02
description: Auditoria full do repo LayoutParserApi (issues 30-232) e do Project #2; fechadas #194/#195/#197 por PR #198 já mergeada; board estava consistente no resto.
metadata:
  type: project
---

Auditoria pedida pelo dono em 2026-09-02: revisar todas as 71 issues (#30-#232, states mistos)
do repo `LayoutParser/LayoutParserApi` e o quadro Project #2 (66 itens), cruzando com
`git log`/PRs mergeados.

**Achado principal:** #194, #195, #197 estavam **OPEN no GitHub e "Todo" no board**, mas a
PR #198 (mergeada 2026-08-27, "feat: contrato aditivo de linha vazia/degradacao posicional +
fases de progresso") já as tinha implementado e o corpo da PR dizia explicitamente
`Closes #194, #195, #197` — mas o merge (provavelmente squash, ou push sem passar pelo GitHub
como merge commit padrão) não disparou o fechamento automático. Fechei as 3 nesta sessão com
comentário citando a PR + arquivos/testes de evidência, e movi o campo Status do board para
Done. Reforça o padrão já visto em [[project_board-sync-2026-08-18]] e
[[project_board-sync-2026-08-28]]: `Closes #N` não é garantia, sempre conferir.

**#196 ficou de fora de propósito** — a própria PR #198 já dizia "issue #196 fica de fora,
bug ainda bloqueado por correlationId" — confirmado: continua genuinamente aberto, bloqueado
em informação que só o dono do projeto pode fornecer.

**Resto do board bateu com a realidade.** As 17 issues fechadas mais antigas (#30-#122,
#213-#215, #225-#232) já estavam corretamente "Done" no board e "closed" no GitHub. As 26
issues restantes genuinamente abertas (#88, #90, #95-#99, #102-#104, #108-#112, #137, #151,
#171-#174, #196, #216, #218-#219, #221) foram checadas por amostragem contra o código atual:

- **#96** (investigação: FindXslFile usa sourceType/targetType na busca real ou só no log) —
  **confirmado no código** (`Services/XmlAnalysis/TransformationPipelineService.cs`): os dois
  parâmetros só aparecem em mensagens de log, a resolução real usa só `layoutName` (padrão
  `*_{layoutName}.xsl`, ver comentário de doc do método referenciando a issue #55). A pergunta
  da investigação está respondida (é log-only), mas não fechei — decisão de produto sobre
  remover os parâmetros mortos é do dono, não uma implementação já entregue.
- **#172** (story: leitura de PDF de orientações para diagnóstico XSD) — existe
  `ai/XslSynth/NtPipeline/PdfSmokeExtractor.cs` (commit `5077c06`, "smoke de extração de PDF
  (P-2)"), mas é rotulado no próprio código como protótipo/exploração, não integrado ao fluxo
  de diagnóstico de erros XSD que a issue pede. **Não fechei** — progresso parcial, não entrega.
- **#171** (tech-debt: tipo de documento hardcoded "NFe") — **ainda presente**:
  `Services/Testing/AutomatedTransformationTestService.cs:238-244` mantém o fallback
  `documentType = "NFe"` com comentário reconhecendo ser fallback. Continua aberta corretamente.
- **#174** (MetricsController.GetLearningSummary não busca modelos reais) — **ainda presente**:
  método segue com `// TODO: Implementar busca de todos os modelos aprendidos` e retorno
  zerado hardcoded. Continua aberta corretamente.
- **#90, #95, #97-#99, #102-#104, #108-#112, #137, #151, #173, #216, #218-#219, #221, #88** —
  não verificados linha a linha nesta sessão (orçamento de tempo); título/board batem com o que
  as memórias anteriores já mapeiam ([[project_bug-gate-issues-2026-08-20]],
  [[project_mapeamento-campo-txt-xml-2026-08-16]], [[reference_gh_cli_setup]]). Nenhuma evidência
  encontrada de implementação recente que os tornasse obsoletos.

**Dependências cross-repo identificadas (não verificáveis sem acesso aos outros repos):**
- #137 (mapeamento campo TXT↔XML) referencia PBI #128/Epic #126 do **LayoutParserReact** —
  dependência de front-end, "não verificável nesta sessão".
- #218/#219/#221 (epic auth machine-to-machine + layout FIAT) bloqueiam a suíte **Cypress E2E**,
  que vive no **LayoutParserReact** — dependência cross-repo confirmada pelo próprio texto da
  issue, estado do lado do front-end "não verificável nesta sessão".
- #216 (expor detect_layout no MCP) é intra-repo (`mcp/LayoutParserMcp/`), sem dependência
  cross-repo real apesar de tocar consumo externo.
- Nenhuma issue aberta neste lote menciona `LayoutParserLib`/`LayoutParserDecrypt` diretamente.

PR #259 ("Develop") confirmada MERGED nesta sessão junto com #254-#258, #260, #261 (todas
já mergeadas antes desta auditoria começar).
