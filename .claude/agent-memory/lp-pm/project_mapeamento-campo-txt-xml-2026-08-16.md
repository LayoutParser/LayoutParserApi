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

**Extensão 2026-08-17** (`docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md`,
`@lp-architect`): a Fase 1 (#139) ganhou o "como" concreto via comentário técnico direto na
issue (não issue nova) — extrair `XslSynth.Contracts.csproj` (classlib sem I/O externo/Ollama)
com `DslStructuredParser`/`StructuredRuleSchema`/`FunctionCatalog`/`GuidXPathCatalog`/
`RealMapperParser`/`MapperVo`, referenciado por `Services/Transformation/` da API via
`ProjectReference` — sem puxar `ai/XslSynth.Core` inteiro (Ollama/RAG) pro caminho crítico
HTTP. Além disso, **#151** (`investigação`, Dono=lp-parser-llm, Status=Todo) formaliza a
Fase 4: reconstrução reversa best-effort XML→TXT. Veredito honesto registrado no corpo da
issue — reversão automática genérica NÃO é prometida (3 riscos: funções não-bijetoras tipo
dígito verificador, agregações N:1 ambíguas, condições que dependem de dado de origem que
some no output). Depende de #139+#140; critério de aceite foca em sinalizar campos
não-reversíveis, não em "sempre funciona".

Related: [[reference-gh-cli-setup]]
