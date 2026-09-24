---
name: plano-tecnico-backlog-2026-09-02
description: Desenho arquitetural das 17 issues pendentes do backlog, feito durante o fine-tuning do Ollama; achados de sequenciamento/overlap entre issues
metadata:
  type: project
---

Documento: `docs/architecture/plano-tecnico-backlog-pendente-2026-09-02.md` (branch
`docs/plano-tecnico-backlog-pendente-2026-09-02`, a partir de `develop` @ `a95778a`, commit local
`96ee119`, sem push — dono decide se vira PR).

**Achados que não eram óbvios antes desta sessão:**
- **#97 pode já estar parcialmente resolvida** por `feat/ai-user-session-schema-102` (PR #270,
  já mergeada em `develop`) — antes de implementar #97, confirmar se o schema
  `AiUserSession`/histórico já existe; senão há risco de recriar trabalho.
- **#90 e #173 compartilham a mesma classe de problema** ("capacidade anunciada vs capacidade
  real" / "validação rasa demais para confiar"), recomendo sequenciar #90 antes — um gate de
  capacidade no boot pode revelar dependências silenciosamente ausentes que também afetam
  `TransformationValidatorService`.
- **#96 e #173 tocam arquivos vizinhos** (`TransformationPipelineService.cs` /
  `TransformationValidatorService.cs`) em sequência curta — considerar PR único ou sequenciado
  pra evitar conflito de merge.
- **#216 (detect_layout no MCP) não tem bloqueio real do lado API** — a detecção de tipo já existe
  inline em `ParseController` (`_layoutDetector.DetectType`); falta só extrair pra endpoint próprio
  + tool MCP. O pedaço incerto é `suggestedLayouts` (score de matching contra catálogo) — pode não
  existir como operação isolada hoje, recomendo MVP sem isso se não existir.
- **#103 (autoria fiscal assistida)** reaproveita a decisão two-step já registrada em
  [[session-artifacts-sharing-design]] (extrair regra estruturada → gerar XSLT a partir da regra,
  não do exemplo cru) — não é decisão nova, é a primeira vez que ela vira desenho de issue
  específica.
- Cross-repo (#221/#219/#218/#137): a parte que cabe à API (contrato do endpoint m2m, contrato de
  `field-mapping`) já pode ser desenhada e implementada sem esperar o LayoutParserReact — só a
  validação Cypress e2e e o consumo de UI ficam de fato bloqueados.

**Como aplicar:** próxima sessão que for implementar qualquer uma das 17, ler a seção
correspondente do documento antes de desenhar do zero — evita retrabalho de investigação já feita
aqui (leitura de `TransformationValidatorService.cs`, `FindXslFile`, `ParseTools.cs`, etc.).
