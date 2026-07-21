---
name: frontend-transformation-tab-built
description: A aba "XML Transformação Final" (dentro do quadrante l-bottom-right) já existe e está funcional — não é trabalho em aberto
metadata:
  type: project
---

Em 2026-07-21 descobri (o dono do projeto não sabia/não mencionou) que o front-end já tem, implementado e
aparentemente funcional:

- `LayoutParserReact/src/components/analysis/AnalysisModeTabs.tsx` — usa `shared/Tabs.tsx` (o "Tabs component
  ocioso" citado pelo dono do projeto — na verdade já está em uso) para alternar entre "TXT Posicional"
  (FieldDisplay+StructureTree) e "XML Transformação Final" (`XmlTransformationDisplay`), esta última só
  aparece se `mapperAvailable` (checado via `GET /api/mapperdatabase/by-input/{layoutGuid}`).
- `AnalysisModeTabs` é renderizado dentro do quadrante `l-bottom-right` do layout em L
  (`LayoutParserPage.tsx`), condicionado a `parseResult.success && txtFile`.
- `XmlTransformationDisplay.tsx` já chama `transformationService.executeTransformation` (Pathway 2 — ver
  [[transformation-pathway-duplication]]), mostra o XML formatado ou o erro; **não mostra nenhum diagnóstico
  IA** — só sucesso/erro cru.
- Comentário no código (`transformationService.ts`) datado "Rotas validadas em 2026-07-20 contra a API real":
  isso é muito recente (véspera desta descoberta) — bom sinal de que está testado contra API real, não só
  mockado.

**Why importa:** um pedido do dono do projeto em 2026-07-21 tratava a existência dessa aba como uma decisão
de design em aberto ("5ª área nova vs. toggle dentro de l-bottom-right?"), a ser resolvida por um prompt futuro
pro `lp-front-dev`. Não é mais uma decisão — já foi tomada e implementada. O trabalho restante do lado do
front é bem mais estreito: estender `XmlTransformationDisplay` (ou componente irmão) para acionar/mostrar o
diagnóstico Ollama quando a validação falhar — não "criar a aba".

**How to apply:** antes de propor qualquer prompt pro `lp-front-dev` sobre essa área, ler estes 3 arquivos
primeiro para não redesenhar o que já existe. Se o Pathway alvo mudar (ver [[transformation-pathway-duplication]]),
`transformationService.ts`/`XmlTransformationDisplay.tsx` precisam de ajuste de contrato, não são "trabalho
zero" mesmo já existindo.
