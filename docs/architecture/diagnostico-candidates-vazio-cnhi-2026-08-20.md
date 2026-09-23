# Diagnóstico — `candidates: []` para LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe (2026-08-20)

> Autoria: `@lp-architect` (Aria). Missão `review-arch`, pedido cross-repo do front-end
> (LayoutParserReact, Epic #87/PBI #92). Não implementa nada — só confirma código já existente.

## Veredito

**Este layout específico já foi investigado a fundo em 2026-08-12** (4 capítulos, ver
`.claude/agent-memory/lp-backend-dev/execute-candidates-ausencia-total-para-cnhi-envnfe.md`) e
originou as issues #38/#39/#40. **As 3 causas identificadas já têm fix aplicado e confirmado
presente no código atual desta branch** — não é o mesmo padrão de causa do dia de hoje
(config `LowCode:AllowedPackageGuids`/`RunnerPath` ausente, issues #107/#108); é causa **diferente**,
já corrigida em código em sessão anterior:

| # | Causa original | Fix | Confirmado no código hoje |
|---|---|---|---|
| 1 | Exceção de SQL em `GetMappersByLayoutGuidForPackagesAsync` engolida como lista vazia — indistinguível de "mapper não existe" | `MapperDatabaseService.cs:341-345` relança a exceção em vez de `return mappers` | Sim — comentário `"NÃO degrada aqui"` presente linha 343 |
| 2 | Pathway tcl-xsl procurava `MAP_{layoutName}.xml` em `Mapeamentro/` (pasta inexistente em produção); artefato real é `{layoutName}.tcl` em `TCL/` | `TransformationPipelineService.cs:28,387` usa `_tclBasePath`/`TransformationPipeline:TclPath` | Sim |
| 3 | Pathway IA (Ollama/RAG) nunca era chamado em `execute-candidates` | `TransformationExecutionController.cs:399-455` (`TryEnqueueAiFallback`) dispara IA no Estado A (`FailureKind.NotApplicable`), suprime no Estado B (`FailureKind.ExecutionInfraError`) | Sim — inclusive a distinção A/B do design de 2026-08-16 está implementada, cobrindo exatamente o cenário de exceção de SQL do item 1 (classificada como infra, corretamente NÃO dispara IA) |

Se o front ainda observa `candidates: []` para este layout **na branch/deploy atual**, a causa não
é mais nenhuma das 3 originais — é preciso reproduzir de novo com log estruturado (`CorrelationId`)
para saber qual warning específico volta hoje (`"Pathway sysmiddle falhou: ..."` vs `"Nenhum
mapeador low-code encontrado..."` vs algo novo).

## Os 4 pontos do pedido do front

**1. Catálogo — layoutGuid resolve pro mapper certo?** Confirmado por SQL do dono em 2026-08-12
(capítulo 2/3 da memória): `InputLayoutGuid = 'LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1'` tem 2
mappers reais em `tbMapper` (`MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE` e
`MAP_CNHI_MQSERIES_RET_LOGTRACE_TXT_TXT_NFE_3.1`), `ProjectId=2` e `PackageGuid` dentro de
`LowCode:AllowedPackageGuids` — catálogo está correto. Não é "mapper não existe".

**2. Resolução de caminho do `.MAP`:** hoje `LoadMappingFileAsync`
(`Services/XmlAnalysis/TransformationPipelineService.cs:382`) monta
`Path.Combine(_tclBasePath, $"{layoutName}.tcl")` — sem transformação de case, usa `layoutName`
literal do catálogo. Convenção real confirmada no dump de produção: `tcl/{layoutName}.tcl`,
nome exato do layout, extensão `.tcl` apesar do conteúdo ser um `<MAP>` XML. `FindXslFile`
(~linha 399) continua com gap conhecido e não corrigido (fallback "primeiro XSL da pasta") — não
afeta `candidates: []` porque só importa depois que o `.tcl` já resolveu.

**3. Mapper publicado ou não:** confirmado publicado (ver ponto 1) — não é hipótese em aberto,
já tem SQL executado e resultado documentado.

**4. Fallback de IA:** wiring existe e está correto (`TryEnqueueAiFallback`, chamado logo após os
dois pathways síncronos terminarem). A classificação `FailureKind` é feita na origem de cada
pathway (não por regex sobre mensagem sanitizada) — cobre exatamente o cenário deste layout: se o
SQL falhar, isso vira `ExecutionInfraError` e a IA **não** dispara (correto por design — não faz
sentido a IA tentar recriar um mapper que já existe só porque a infra falhou). Se o front espera
IA disparar sempre que `candidates: []`, isso é uma expectativa a alinhar — o design distingue
"não modelado" (dispara IA) de "existe mas infra falhou" (não dispara, é problema operacional).

## Próximo passo (se o sintoma persistir)

Não há SQL/host novo a rodar — a query de 2026-08-12 já confirmou o dado. Próximo passo é
**reproduzir a chamada real e capturar o `CorrelationId`/log estruturado** do momento exato, para
ver qual mensagem volta hoje. Se vier `"Pathway sysmiddle falhou: ..."`, é uma exceção de infra
nova (conexão, timeout, runner) — não uma regressão das 3 causas antigas.
