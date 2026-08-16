---
name: project-mapeamento-campo-txt-xml-2026-08-16
description: Issues #137-#141 do plano de mapeamento campo TXT<->XML (PBI #128/Epic #126 do front-end), origem docs/architecture de 2026-08-16
metadata:
  type: project
---

Formalizadas em 2026-08-16, missão `story-from-decision`, a partir de dois docs de
`@lp-architect`: `docs/architecture/resposta-mapeamento-campo-txt-xml-2026-08-16.md` e
`docs/architecture/plano-execucao-mapeamento-campo-txt-xml-2026-08-16.md`. Dono confirmou o
plano e pediu formalização explícita ("manda a pia formalizar tudo como pbi e as fases de dev").

Estrutura criada (todas no Project #2, Status=Todo):

- **#137** — guarda-chuva (`story`, Dono=lp-architect). Linka os 4 sub-itens e cita #103/#39
  como relacionados não-duplicados.
- **#138** — Fase 0/Opção 3 (`story`, Dono=lp-backend-dev): generalizar `SegmentMappings` →
  `sectionMappings` nos pathways sysmiddle/tcl-xsl. Independente das demais, pode começar já.
- **#139** — Fase 1 (`investigação`, Dono=lp-parser-llm): confirmar shape real do `MapperVO` de
  produção + decidir se `RealMapperParser` (`ai/XslSynth.Core`) é promovido a runtime ou se
  nasce parser novo. Bloqueia #140.
- **#140** — Fase 2 (`story`, Dono=lp-parser-llm): catálogo `TargetLayoutGuid`→XPath + DSL N:1 +
  grupos repetidos. Marco bloqueante: validação comportamental por valor contra o
  `LayoutParserLowCodeRunner.exe` real, amostra >=20 docs. Depende de #139, bloqueia #141.
- **#141** — Fase 3 (`story`, Dono=lp-backend-dev): expor `fieldMappings` opcional no contrato
  HTTP. Depende de #140.

**Por que**: pedido original do front (`fieldMappings` campo a campo) foi considerado NÃO
VIÁVEL a curto prazo — a resolução origem→destino só existe dentro do `.exe` de terceiro, sem
canal de contato com o fornecedor (Opção 2 antiga descartada pelo dono). A validação
comportamental da Fase 2 assume esse papel de "fonte de confiança" no lugar do fornecedor.

**Como aplicar**: se qualquer uma dessas fases evoluir (PR aberto, achado técnico novo,
mudança de escopo), atualizar/comentar a issue existente em vez de abrir nova — não duplicar
o plano. Se o dono perguntar por "o board da Lia/Dex para isso", são #138-#141.

Related: [[reference-gh-cli-setup]]
