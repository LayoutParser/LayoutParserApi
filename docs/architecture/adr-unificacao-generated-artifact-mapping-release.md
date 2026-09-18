# ADR — Unificação de `GeneratedMapperArtifact` e `MappingRelease`

- **Status:** Proposto
- **Data:** 2026-09-18
- **Autor:** `@lp-architect` (Aria)
- **Contexto disparador:** issue #438 ("Pendências conhecidas"), priorizada pelo dono em 2026-09-18
  enquanto ~69 mapeadores reais do catálogo Sysmiddle estão sendo gerados em paralelo e precisam
  ficar visíveis pro time de operações.

## 1. Problema

Hoje existem dois domínios de "TCL/XSL/XSLT gerado", com forma, ciclo de vida e endpoint
completamente diferentes:

| | `MappingRelease` (governança) | `GeneratedMapperArtifact` (automático) |
|---|---|---|
| Chave | `workspaceId` + `draftId` (humano cria o draft) | `mapperGuid` puro |
| Como nasce | `POST .../compile` explícito sobre um `MappingDraft` | lazy, disparado no primeiro `GET` do endpoint, fire-and-forget |
| Ciclo de vida | `draft_compiled → test_passed/test_failed → in_review → approved → published → deprecated/archived` | `none → generating → ready/stale` |
| RBAC | `RequireWorkspaceRole` (owner/fiscal_admin/mapper/reviewer/operator/viewer) | nenhum ainda |
| Cobertura/validação | `MappingTestRunSummary` — Fiscal Test Lab real (XSD + diff canônico contra gabarito) | `CoverageJson` rotulado `validationBasis="declared_dsl"` — cobertura contra o próprio DSL declarado, **nunca execução real** |
| Armazenamento | `dbo.tbMappingRelease` (`SqlMappingReleaseStore`) | `dbo.tbGeneratedMapperArtifact` (`SqlGeneratedMapperArtifactStore`) |
| Consumo hoje | `GET /api/workspaces/{workspaceId}/mapping-releases` — **é o que o React já lista** | `GET api/workspaces/{workspaceId}/mappings/{mapperGuid}/generated-transformation` — endpoint isolado, front ainda não usa em lugar nenhum visível |

O dono quer que o conteúdo gerado automaticamente apareça no **mesmo catálogo** que o React já
consulta, para o time de operações ver os ~69 artefatos sem precisar aprender um segundo lugar.

## 2. Opções avaliadas

### (a) Sintetizar um `MappingDraft`/`MappingRelease` "system-owned" a cada geração automática

Criaria, sem intervenção humana, um `MappingDraft` de propriedade de um workspace/usuário
sintético ("sistema"), e compilaria como `MappingRelease` real toda vez que
`GeneratedMapperArtifactService` termina uma geração.

**Rejeitada.** Motivos:

- `MappingRelease` carrega um ciclo de vida de governança inteiro (`RequireWorkspaceRole`,
  `ApprovedByUserId`, `PublishedByUserId`, `FiscalProfile` snapshot, `RulesSnapshotHash` sobre
  regras de um `MappingDraft` real, `MappingTransition` auditável) que não tem correspondente
  no fluxo automático — o gerador não tem regras estruturadas aceitas/editadas por um humano,
  não tem workspace de negócio, não tem `FiscalProfile`. Forçar esses campos a existir de forma
  sintética degrada o significado deles para todo consumidor futuro (auditoria, RBAC, "quem
  aprovou isso?" deixa de ter resposta confiável).
- `TestRunSummary` de uma `MappingRelease` hoje implica Fiscal Test Lab real (XSD + diff
  canônico). O artefato automático só tem `validationBasis="declared_dsl"` — colar isso dentro
  de `MappingRelease` sem um `TestRunSummary` real cria um estado que o resto do sistema
  (dashboards, gates de `approve`) não sabe distinguir de uma release testada de verdade, a
  menos que se invente um novo status/flag — nesse ponto já se está reimplementando o
  discriminador que a opção (b) propõe de forma mais barata.
- Maior esforço de migração: exige workspace sintético, usuário sintético, e uma extensão do
  RBAC/`RequireWorkspaceRole` para não travar a escrita "do sistema".

### (b) View agregada em `GET .../mapping-releases` — ESCOLHIDA

Mantém os dois modelos de dado como estão (cada um continua representando exatamente o que é:
draft compilado versus geração automática), e estende **só a leitura** — o endpoint que o front
já consulta passa a agregar as duas fontes, com um campo discriminador `origin`.

**Por que esta é a escolha:**

- **Menor esforço de migração.** Não migra nenhum dado existente, não toca no schema de
  `tbMappingRelease`/`tbGeneratedMapperArtifact`, não muda o `SqlMappingReleaseStore`. O único
  código novo é na composição da resposta do `List` do `MappingGovernanceController` (ou um
  decorator/serviço de agregação na frente dele).
- **Não quebra quem já usa o fluxo manual de draft.** Todo consumidor atual de
  `mapping-releases` (filtros por `status`/`draftId`/`environment`, RBAC por workspace) continua
  funcionando sem alteração — os itens `origin: "draft_compile"` são exatamente os
  `MappingRelease` de hoje, byte a byte.
- **Front aprende um contrato só.** Em vez de o React ter que consultar dois endpoints e
  mesclar client-side (lógica que inevitavelmente duplica no front o que deveria ser
  responsabilidade do backend), ele continua chamando `GET .../mapping-releases` e recebe um
  campo `origin` para decidir como renderizar (ex.: esconder botões de "aprovar"/"publicar"
  quando `origin=auto_generated`, já que esse ciclo de vida não existe pra ele).
- **Preserva a honestidade dos dados.** `GeneratedMapperArtifact` nunca fingirá ter passado pelo
  Fiscal Test Lab — o campo `validationBasis` viaja explícito na resposta (ver §3), em vez de
  ser absorvido silenciosamente num campo que hoje significa "testado de verdade".

**Trade-off aceito:** a listagem deixa de ser 1:1 com uma única tabela SQL — passa a ser uma
composição de duas fontes, com paginação combinada (ver limitação em §4). É um custo de
implementação pontual, não recorrente, e razoável dado que o volume de
`GeneratedMapperArtifact` (1 por `mapperGuid`, ~69 hoje, cresce com o catálogo Sysmiddle, não
com o tráfego) é ordens de grandeza menor que releases manuais poderiam vir a ser em um workspace
ativo.

### (c) Outra abordagem considerada e descartada: endpoint novo, sem tocar em `mapping-releases`

Só adicionar um badge/link no front apontando pro endpoint já existente
(`.../mappings/{mapperGuid}/generated-transformation`), sem mexer no backend.

**Rejeitada** por não atender o pedido explícito do dono ("deveriam aparecer nos releases") —
manteria dois lugares visíveis, só que agora linkados entre si; não resolve a fragmentação, só a
esconde uma camada a mais.

## 3. Contrato de resposta unificado

### 3.1 Item da listagem (`GET /api/workspaces/{workspaceId}/mapping-releases`)

Adiciona `origin` como primeiro campo discriminador. Os campos que só fazem sentido para o
ciclo de vida de `MappingRelease` são **omitidos** (não `null`) quando `origin=auto_generated` —
`null` sugeriria "esse dado existe mas está vazio"; omitir deixa claro que o conceito não se
aplica. Segue a mesma convenção já usada no projeto (`DefaultIgnoreCondition = WhenWritingNull`
não basta sozinho aqui — a serialização precisa ser condicional por `origin`, então a
composição monta o objeto anônimo/DTO por branch em vez de depender só da opção global).

```jsonc
// origin = "draft_compile" — formato atual, sem nenhuma mudança de campo
{
  "origin": "draft_compile",
  "releaseId": "…",
  "workspaceId": "…",
  "draftId": "…",
  "engine": "xslt",
  "status": "test_passed",
  "testRunSummary": { "passed": 12, "failed": 0, "coveragePercent": 100.0, "requiredGatesPassed": true, "xsdValid": true, "...": "..." },
  "createdAt": "2026-09-18T12:00:00Z",
  "approvedByUserId": null,
  "publishedAt": null,
  "environment": "development"
  // ...demais campos de MappingRelease, sem alteração
}

// origin = "auto_generated" — novo formato agregado
{
  "origin": "auto_generated",
  "mapperGuid": "3F2A1C90-…",
  "mapperName": "NFe_Fiat_Layout_v3",       // resolvido via ICachedMapperService, conveniência pro front
  "engine": "xslt",                          // GeneratedMapperArtifact hoje produz um candidato único; ver nota abaixo
  "status": "ready",                         // mapeado de GeneratedMapperArtifactStatus (ready/generating/stale)
  "validationBasis": "declared_dsl",         // OBRIGATÓRIO, nunca omitido — é o alerta central do §3.3
  "coverage": { "compiles": true, "linkPct": 0.87, "rulePct": 0.92, "...": "..." },
  "generatedAt": "2026-09-18T09:30:00Z",
  "correlationId": "…"
  // SEM: releaseId, workspaceId (o do path continua valendo pro filtro, mas não é "dono" do artefato),
  //      draftId, testRunSummary, approvedByUserId, publishedAt, environment, fiscalProfile —
  //      nenhum desses conceitos existe no fluxo automático.
}
```

Notas de implementação:
- `mapperGuid` cumpre o papel de identificador estável que `releaseId` cumpre no outro branch —
  o front usa esse campo para montar links/expandir detalhe, então o contrato precisa garantir
  que todo item tenha *algum* identificador, ainda que o nome do campo mude por `origin`.
- `engine`: o `GeneratedMapperArtifactService` atual gera um candidato único de saída (XSLT/TCL
  combinado, ver `SynthesizeAsync`); ainda não separa por engine como `MappingRelease.Artifacts`
  faz. Isso é uma divergência real de forma, não um detalhe de serialização — registrada aqui
  para não ser perdida (ver §4, fora do escopo mínimo resolver agora).

### 3.2 Paginação combinada

`page`/`pageSize`/`totalCount` continuam existindo, mas passam a somar as duas fontes. Duas
estratégias possíveis:
- **Simples (recomendada para o escopo mínimo):** buscar todos os `GeneratedMapperArtifact` do
  workspace (baixo volume, ~69 hoje, sem paginação própria — `IGeneratedMapperArtifactStore` já
  não pagina), fixá-los como um bloco no topo ou rodapé da primeira página, e paginar só o lado
  `MappingRelease` como hoje. Simples de implementar, correto o bastante enquanto o volume de
  artefatos automáticos for baixo.
- **Correta a longo prazo:** ordenar por timestamp (`createdAt`/`generatedAt`) e paginar sobre a
  união ordenada — exige uma query/merge explícito. Adiar até o volume justificar.

### 3.3 `filter` novo: `origin`

Estende o mesmo padrão de `status`/`draftId`/`environment` já validado no controller — `origin`
aceita `draft_compile` | `auto_generated`, ausente = ambos. Não quebra chamadas existentes
(filtro ausente = comportamento atual, igual ao padrão já usado pelos outros filtros da issue
#377).

## 4. `validationBasis`/`coverageJson` — como não perder a informação honesta

Este é o ponto mais sensível da unificação: `GeneratedMapperArtifact.ValidationBasis =
"declared_dsl"` existe precisamente para deixar claro que a cobertura reportada é calculada
contra o próprio `MapperVo` declarado, **nunca contra execução real do XSLT/TCL** (sem
gabarito, sem XSD, sem Fiscal Test Lab) — ao contrário de `MappingTestRunSummary.XsdValid`/
`RequiredGatesPassed`, que são resultado de execução real.

Decisões:
- `validationBasis` viaja **sempre presente e nunca omitido** no item `origin=auto_generated` —
  é o principal sinal para o time de operações não confundir "gerado e sintaticamente coberto"
  com "testado e aprovado". Recomendação de UX pro React: badge visualmente distinto (ex.: cor
  âmbar "cobertura declarada, não testada" vs. verde "Fiscal Test Lab: X/Y passou").
- `coverage` (renomeado de `coverageJson`, desserializado no backend antes de expor — hoje é uma
  string JSON opaca; expor como objeto estruturado evita o front ter que fazer `JSON.parse` de
  novo) mantém sua forma própria (`compiles`, `linkPct`, `rulePct`, `provenanceEntries`) — **não**
  é remapeado para o formato de `MappingTestRunSummary` (que tem `passed`/`failed`/
  `divergences`, campos que não fazem sentido sem execução real). Misturar os dois formatos sob
  o mesmo nome de campo seria o mesmo erro de honestidade que a opção (a) cometeria em outra
  camada.
- Nenhum item `auto_generated` deve, em nenhuma circunstância, ser elegível para os endpoints de
  `approve`/`publish`/`rollback` do `MappingGovernanceController` — esses continuam operando
  exclusivamente sobre `releaseId` de `MappingRelease` real. Se o dono quiser no futuro promover
  um artefato automático para o ciclo de governança completo, isso é uma ação humana explícita
  e nova ("promover para draft"), não parte desta unificação de leitura.

## 5. Escopo mínimo para a próxima rodada

Dado que ~69 artefatos já estão sendo gerados e precisam de visibilidade imediata:

1. **Backend** (`@lp-backend-dev`):
   - Novo método de agregação (ex.: `IMappingReleaseAggregationService` ou lógica direta no
     `MappingGovernanceController.List`) que busca `MappingRelease` (como hoje) +
     `GeneratedMapperArtifact` do workspace, projeta cada um no formato de §3.1, concatena.
   - "`GeneratedMapperArtifact` do workspace" exige resolver **quais mappers pertencem a qual
     workspace** — hoje `GeneratedMapperArtifactService`/`SqlGeneratedMapperArtifactStore`
     trabalham por `mapperGuid` puro, sem `workspaceId`. Checar se existe hoje uma associação
     mapper↔workspace reaproveitável (catálogo Sysmiddle, `ICachedMapperService`) antes de
     inventar uma nova tabela de vínculo — **isso é o item que pode furar o escopo mínimo**, vale
     confirmar com `@lp-backend-dev` antes de estimar.
   - Adicionar filtro `origin` na query string (validação simples, mesmo padrão dos filtros
     existentes).
   - Paginação: usar a estratégia simples de §3.2 (bloco fixo, sem merge ordenado) para esta
     rodada.
2. **Front** (`LayoutParserReact`): consumir `origin` no item já listado por
   `mapping-releases` — esconder ações de governança (`approve`/`publish`/`compile`) quando
   `auto_generated`, mostrar badge de `validationBasis`. Não precisa de tela nova.
3. **Fora do escopo mínimo, registrado para depois:** merge ordenado real de paginação;
   separação de `engine` (tcl vs xslt) dentro de `GeneratedMapperArtifact`, hoje um candidato
   único; caminho de "promover artefato automático para `MappingDraft` humano" (ação explícita,
   não implícita nesta unificação).

## 6. Decisão

Adotar **opção (b)** — view agregada em `GET .../mapping-releases`, com campo discriminador
`origin`, sem migrar dado nem estender o ciclo de vida de `MappingRelease` para cobrir geração
automática. `GeneratedMapperArtifact` continua representando exatamente o que é: um candidato
gerado automaticamente, com cobertura declarada, não testada — nunca uma `MappingRelease`
disfarçada.
