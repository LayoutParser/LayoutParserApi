# Resumo da sessão 2026-08-30/31

Fontes: `git log --oneline --since="2026-08-30" -- docs/architecture/`, `gh pr list --state merged`,
`gh issue view` de cada issue citada, `gh issue list`. Datas reais de merge conferidas via `gh pr view`
(alguns itens do briefing original têm data de 08-29, não 08-30/31 — corrigido abaixo, sinalizado).

## Linha do tempo

| # | Item | Status | Evidência |
|---|------|--------|-----------|
| 1 | Fix `InformacoesParaEDI` (Length) + `OccurrenceCount`/`IsAggregatedOccurrence` | Mesclado | commit `99065a0` "feat(parsing): sinais aditivos de linha... + fix Bug A/B InformacoesParaEDI" — PR #191 (referenciado em memória de `@lp-pm`, não reconfirmado nesta auditoria) |
| 2 | PR #217 — Swagger/XML docs completo | **Mesclado 2026-08-29T21:04:05Z** (não 08-30/31) | `b756630` Merge PR #217, `ea1d41e` docs(swagger) |
| 3 | Estudo migração Linux+Ollama | Concluído (doc) | `9f0fcdc`/`b8610b8` "estudo de migracao Linux + Ollama" — conclusão: LowCodeRunner fica preso a Windows (net481 x86, interop nativo Sysmiddle) |
| 4 | Decisão DSL Sysmiddle via ILSpy | Concluído (doc, **08-21**, não desta sessão) | `f748628`/`d0c6b69` `docs/architecture/decisao-dsl-mapper-sysmiddle-2026-08-21.md` — decisão: interpretador proprietário line-based, nunca Roslyn |
| 5 | Integração `RepairOrchestrator` (XSLT real via Ollama) | **Mesclado 2026-08-29T14:05:57Z** (não 08-30/31) — PR #211 | `23b2de0` Merge PR #211; sequência de commits `0c4ccb9`→`6239639` fecha gap de input XML e regressão de latência síncrona |
| 6 | Visão migração Sysmiddle→TCL/XSLT (259 pares humanos) | Concluído (doc) | `904a5c6`/`5347d47` — 2026-08-30 |
| 7 | Fine-tuning: reversão decisão 21/07, smoke-tests #1-#4, treino 3 épocas (overfitting), diagnóstico degeneração por época | Concluído até diagnóstico; **treino corrigido não confirmado como concluído** | `9253881`→`817bfad`, último commit da série é o diagnóstico de degeneração (truncamento de prompt a 1024 tokens como causa suspeita), sem commit posterior indicando "treino corrigido rodou e validou" |
| 8 | Correção `Ollama:Url` órfão (localhost → IP fixo VM) | **NÃO CONFIRMADO — aparenta não ter sido aplicado** | `appsettings.json` no worktree atual ainda tem `"Url": "http://localhost:11434"`; `git log -S "172.25.32.5" -- appsettings.json` não retorna nenhum commit. Não há evidência de que essa correção foi de fato commitada nesta branch. Divergência real entre o que o briefing afirma e o estado do arquivo — sinalizar ao dono, não assumir feito. |
| 9 | Fundação plataforma fiscal — Slice 1 | **CONCLUÍDO** — PR #234 mesclado 2026-08-31T17:28:18Z, fecha #225/#228 | ver auditoria de slices abaixo |

## Auditoria de slices — plataforma fiscal (prompt de 2026-08-31)

Texto original preservado em `docs/architecture/spec-plataforma-fiscal-prompt-original-2026-08-31.md`.

| Slice | Issue(s) | Status real | Evidência / o que falta |
|-------|----------|--------------|--------------------------|
| **1 — Identidade/workspace** | #225, #228 | **CONCLUÍDO** | PR #234 mesclado (`ExternalIdentity`, `FiscalUser`, `FiscalWorkspace`, `WorkspaceMembership`, `TrustedIdentityMiddleware` com 3 headers novos, `GET /api/workspaces/me`, `GET /api/workspaces/{id}`). 496 testes verdes (437+59). Gate QA PASS (isolamento cross-workspace, fail-closed, subject não vaza). **Limitação documentada**: idempotência multi-instância não testada contra SQL Server real (só ambiente local) — pendência explícita, não bloqueante. |
| **2 — `FiscalMappingPackage`** | #229 | **NÃO INICIADO** | Issue aberta, sem comentários, projeto = Backlog/Todo. Falta: persistência versionada do pacote, revisão imutável (hash/autor/instante/classificação/retenção), validação de conteúdo/MIME real + limite + antimalware, inventário normalizado de campos/XSD/colunas de planilha com IDs estáveis, retorno de conflitos/ausências sem inferência silenciosa, upload idempotente e isolado por workspace, garantia de que conteúdo bruto não aparece em log/erro, contrato OpenAPI + fixture sanitizada. Nenhum código associado encontrado. |
| **3 — `MappingDraft` human-in-the-loop** | #230 | **NÃO INICIADO** | Issue aberta, sem comentários. Falta: modelo `MappingDraft` referenciando revisão imutável do pacote, regras com source/target/operação/condição/evidência/confiança/limitações/perguntas, máquina de estados (`proposed/accepted/edited/rejected/needs_input/validated/superseded`), aceitar/editar/rejeitar via ETag/`If-Match`, auditoria de autoria/instante/revisão/justificativa, geração restrita a regras aceitas/editadas, job cancelável/observável/idempotente, recusa categórica de `engine=sysmiddle`, política anti-envio externo indevido. |
| **4 — `MappingExplanation` + explicabilidade Sysmiddle** | #226, #227 (sub-issue #232) | **NÃO INICIADO** (investigação aberta, sem execução) | #226: contrato canônico `MappingExplanation` (mapping/version, schemas, regras ordenadas, sources/targets/conditions/operations, cardinalidade, evidence, supportLevel, limitations, capabilities) — nada implementado. #227: investigação de explicabilidade read-only do Sysmiddle, tem 1 comentário mas sub-issue #232 (gate de negação de mutação) ainda aberta e sem trabalho. Falta adapter XSL/XSLT, adapter TCL com parser/AST real, IDs estáveis de regra, determinismo em fixtures, degradação pra `opaque` em código desconhecido. |
| **5 — Compilação TCL/XSL/XSLT + Fiscal Test Lab** | #231 | **NÃO INICIADO** | Issue aberta, sem comentários. Falta: compilação assíncrona/idempotente/versionada, diagnóstico sintático ligado a `MappingDraftRule`, execução de fixture individual e suite, validação XSD+fiscal versionada, diff XML canônico, cobertura de destinos obrigatórios, provenance saída→regra→origem, correlation ID ponta a ponta, imutabilidade de versão publicada + bloqueio de regressão, recusa total de engine Sysmiddle. |
| **6 — Governança admin (CRUD/promoção de mapeadores)** | #94 (issue #206 citada no briefing **não existe** no repositório) | **NÃO INICIADO** — nem desenho ainda | #94 é hoje só rastreamento/design: aceite exige que `@lp-architect` produza desenho de 3 endpoints de escrita (editar TCL/XSL, promover candidato IA→oficial, revogar/desativar) e modelo de dados que distinga origem "analista" vs. "IA promovida" — nenhum dos dois existe. `Controllers/MapperDatabaseController.cs` hoje só tem leitura + `refresh-cache`. |
| **7 — Gate transversal Sysmiddle "somente execução/explicação"** | #232 (sub-issue de #227) | **NÃO INICIADO** | Depende do Slice 4 (#227) estar minimamente resolvido antes de fazer sentido. Capability `author=false/compile=false/publish=false`, rejeição server-side em rotas genéricas de Draft/compile/release, auditoria sem conteúdo proprietário, testes de adulteração de payload/ID — nada disso existe hoje. |

**Conclusão da auditoria de slices: 1 de 7 concluído (14%).** Os outros 6 estão no estado exato em
que estavam antes da sessão de hoje — issues abertas no backlog, sem comentário de progresso, sem
commit associado. Não há evidência de trabalho parcial "em andamento" em nenhum deles; tratá-los
como "em progresso" seria impreciso.

### Itens transversais do prompt (fora da numeração de slice)

| Item | Status |
|------|--------|
| Atualização de memória/handoff | Parcial — memória de `@lp-parser-llm` foi tocada nesta sessão (`.claude/agent-memory/lp-parser-llm/MEMORY.md` modificado, arquivo novo `pr209-falso-conflito-develop-stale.md`); memória de `@lp-pm` sendo atualizada agora com esta auditoria. |
| README/documentação atualizada pelo Slice 1 | **NÃO** — `gh pr view 234 --json files` não lista nenhum arquivo `README.md` entre os 17 arquivos alterados. Só foi criado `docs/architecture/auditoria-slice1-identidade-workspaces-2026-08-31.md`. |
| Issues/Project atualizadas com status/evidência (seção 3 do prompt) | Parcial — #225/#228 foram fechadas automaticamente pelo `Closes #225`/`Closes #228` do PR #234 (confirmar se o fechamento automático realmente ocorreu, já que closing keyword só funciona dentro do mesmo repositório — aqui está, então deve funcionar). As demais 6 issues de slice (#229/#230/#226/#227/#231/#232/#94) **não têm nenhum comentário novo** desde a criação — não foram atualizadas com status desta sessão. |
| `@lp-contract-qa` validou entrega pro frontend (seção 16) | **NÃO — pendência real.** Nenhuma menção a `@lp-contract-qa`/"contract-qa" em nenhum doc de arquitetura recente nem no corpo do PR #234 (que cita apenas validação de `@lp-qa`, não do contrato cross-repo com o frontend). Sinalizar como gap antes de considerar o Slice 1 pronto para consumo do `LayoutParserReact`. |

## Estado real do quadro (GitHub Project)

**Issues fechadas com `updated:>=2026-08-30`:**
- #215, #214, #213 — fechadas em 2026-08-30T17:10Z, feature de detecção autoritativa de layout MQSeries/IDoc (fora do escopo da plataforma fiscal, trabalho concluído antes/durante 08-30).
- #225, #228 — fechadas via PR #234 (`Closes #225`, `Closes #228`), 2026-08-31.

**PRs mesclados desde 2026-08-30:**
- #234 (Slice 1 identidade/workspace) — 2026-08-31.
- #233 ("Develop", merge de sincronização) — 2026-08-31.
- #224, #223 (bumps automáticos de dependência/Dependabot) — 2026-08-31.
- #222 (detecção automática de layout por documento) — 2026-08-30.
- Fora da janela 08-30/31 mas citados no briefing: #217 (2026-08-29) e #211 (2026-08-29).

**Issues que deveriam ter sido tocadas e não foram:**
- #229, #230, #226, #227, #232, #94 — todas no milestone "P0 — Plataforma Fiscal e Workspaces",
  todas com `updatedAt` de criação (2026-08-31, no momento em que foram abertas), zero comentário
  desde então. Se a intenção da sessão era avançar a fundação da plataforma fiscal além do Slice 1,
  nenhuma dessas issues reflete isso — não foram comentadas com progresso nem com decisão de
  adiamento explícita.
- Nenhuma issue nova foi criada nesta auditoria (conforme instrução — só sinalização de candidato,
  sem `gh issue create` sem aval).

## Candidato a issue nova (sinalizado, não criado)

O item 8 da linha do tempo ("correção do `Ollama:Url` órfão") aparenta **não ter sido aplicado** —
`appsettings.json` ainda aponta para `localhost:11434`. Se a correção foi de fato feita em outro
worktree/branch e ainda não chegou aqui, não é uma issue nova, é sincronização pendente. Se
realmente não foi feita, é um candidato a `tech-debt`/`bug` ("Ollama:Url ainda aponta para
localhost, não para IP fixo da VM `172.25.32.5`") — proponho, não crio, aguardando confirmação do
dono sobre se a correção já existe em algum branch que não foi verificado aqui.

## Honestidade sobre o escopo

Da fundação da "plataforma fiscal" descrita no prompt de 18 seções, **apenas 1 de 7 slices está
de fato implementado e mesclado** (Slice 1 — identidade/workspace). Os demais 6 permanecem como
issues de backlog sem execução, e a validação de `@lp-contract-qa` exigida pela seção 16 do prompt
para handoff ao frontend não ocorreu — mesmo para o Slice 1 já concluído.

## Pedidos formais do front-end — status real (2026-08-31)

Cruzado com `gh` (não confiado só em memória), repos `LayoutParser/LayoutParserApi` e
`LayoutParser/LayoutParserReact`.

1. **Bug `candidates:[]` — layout CNHI ENVNFe** (docs `diagnostico-candidates-vazio-cnhi-2026-08-20.md`
   / `resposta-frontend-...`). **RESOLVIDO.** Rastreado como React#86 ("[BUG] Diagnóstico: nenhum
   candidato Sysmiddle ou TCL/XSL para layout NFe"), fechado 2026-08-28 com evidência de
   `LayoutParserApi#200` (MERGED) — adiciona diagnóstico estruturado a `execute-candidates`. O
   closing keyword não atravessou repositório (issue no React, PR na Api), fechamento foi manual;
   comentário de 2026-08-29 só corrigiu o campo Status do Project (`Blocked`→`Done`), sem mudança
   técnica nova. Nada pendente aqui — o "próximo passo" do diagnóstico de 20/08 (capturar log com
   `CorrelationId`) foi cumprido pela PR #200.

2. **Contrato `fieldMappings`/`segmentMappings` TXT↔XML** (PBI React#128 / Epic React#126).
   **RESOLVIDO.** Ambos `CLOSED`, Project "Done". Comentário de encerramento (2026-08-29) cita
   entrega via `LayoutParserApi#138/#141`, consumida pelo front nas PRs React#164 e #166
   (`fieldMappings`, `sectionMappings`, `xmlNamespaces`, navegação vinculada), promovida a `main`
   pela PR #167. Ressalva registrada pelo próprio front: validação comportamental com ≥20
   documentos reais contra o `LowCodeRunner.exe` Windows **continua pendente** na Api — por isso a
   UI evita rotular `Authoritative` como "validado em produção". Escopo funcional aceito, essa
   ressalva não tem issue própria.

3. **Bug `InformacoesParaEDI`** (Length + `OccurrenceCount`/`IsAggregatedOccurrence`).
   **RESOLVIDO, confirmado.** `gh pr view 191`: `state: MERGED`, `mergedAt: 2026-08-26T13:04:11Z`
   — resolve a incerteza sinalizada no item 1 da linha do tempo acima.

4. **Documento fonte da plataforma fiscal** (PR React#207) — coberto em
   `auditoria-slice1-identidade-workspaces-2026-08-31.md`; não repetido aqui.

Nenhuma issue nova criada (nenhum item ficou pendente que justificasse).

## Status real dos 7 slices — 2026-08-31 (final)

Reconferido via `gh issue view`/`gh pr view` reais nesta auditoria final (não reaproveitado da
tabela acima, que já estava desatualizada — registrava só o Slice 1).

| Slice | Issue(s) | Status | Evidência |
|-------|----------|--------|-----------|
| **1 — Identidade/workspace** | #225, #228 (OPEN) | **Concluído** | PR #234 `MERGED` (2026-08-31T17:28:18Z). Issues #225/#228 continuam `OPEN` no GitHub apesar do PR mesclado — `Closes #N` não fechou automaticamente (confirmado nesta auditoria, contradiz o que a linha do tempo acima registrou como fechamento automático). |
| **2 — `FiscalMappingPackage`** | #229 (OPEN) | **Concluído** | PR #236 `MERGED` (2026-08-31T18:50:40Z). Issue #229 segue `OPEN`. |
| **3 — `MappingDraft`** | #230 (OPEN) | **Concluído** | PR #238 `MERGED` (2026-08-31T20:51:14Z). Issue #230 segue `OPEN`. |
| **4 — `MappingExplanation`/explicabilidade Sysmiddle** | #226, #227 (OPEN) | **Não iniciado** | Sem PR associado, sem comentário de progresso. |
| **5 — Compilação/Fiscal Test Lab** | #231 (OPEN) | **Não iniciado** | Sem PR associado. |
| **6 — Gate transversal Sysmiddle** | #232 (OPEN) | **Não iniciado** | Sem PR dedicado. O `MappingEngineGuardFilter` entregue no Slice 3 (PR #238) recusa `engine=sysmiddle` **só nos endpoints do próprio Slice 3** (`MappingDraft`/geração) — cobertura parcial e incidental, não os testes abrangentes que #232 pede ("nenhum endpoint, payload adulterado, role ou estado permite mutação" em qualquer rota, presente ou futura). Insuficiente para fechar #232. |
| **7 — Governança/piloto FIAT** | #94 (OPEN) | **Não iniciado** | Sem desenho de `@lp-architect`, sem PR. `MapperDatabaseController` ainda só leitura + `refresh-cache`. |

**Progresso real: 3 de 7 slices = 43%** (Slices 1, 2 e 3 concluídos e mesclados; Slices 4-7 sem
nenhum trabalho iniciado).

### Sincronização de branches

`git diff origin/master origin/develop --stat` vazio — árvores idênticas, **sincronizados** (PR
#239 mesclado). O `git log origin/master..origin/develop` não está vazio por causa de hashes de
merge commit distintos entre os dois lados (mesmo conteúdo, histórico reescrito no merge), não por
divergência real de conteúdo.

### Handoff frontend (`@lp-contract-qa`) — Slices 2 e 3

Não existe agente `@lp-contract-qa` configurado neste repositório (`.claude/agent-memory/` só tem
`lp-architect`, `lp-backend-dev`, `lp-devops`, `lp-doc`, `lp-parser-llm`, `lp-pm`, `lp-qa`) e nenhum
PR (#234, #236, #238) menciona validação de contrato cross-repo com o `LayoutParserReact`. A
pendência do Slice 1 **não mudou** — se estende igualmente aos Slices 2 e 3: nenhum dos três teve
validação de contrato para consumo do frontend.

### README — Slices 2 e 3

`gh pr view 236 --json files` e `gh pr view 238 --json files` não listam `README.md` entre os
arquivos alterados (mesmo padrão do Slice 1, PR #234). Nenhum dos 3 slices mesclados atualizou o
README.

### O que falta, em ordem de prioridade

1. **Fechar issues #225/#228/#229/#230 manualmente** — trabalho está pronto e mesclado, mas o
   board não reflete isso (closing keyword não fechou automaticamente apesar de PR e issue estarem
   no mesmo repositório — merece investigação separada de por que não funcionou).
2. **Slice 4** (`MappingExplanation` + explicabilidade Sysmiddle, #226/#227) — bloqueia o Slice 6
   (gate transversal depende de #227 estar pelo menos desenhado).
3. **Slice 6** (gate transversal Sysmiddle, #232) — hoje só tem cobertura incidental via
   `MappingEngineGuardFilter` do Slice 3; precisa de testes dedicados cobrindo toda rota
   presente/futura, não só as do Slice 3.
4. **Slice 5** (compilação TCL/XSL/XSLT + Fiscal Test Lab, #231) — sem nenhum trabalho.
5. **Slice 7** (governança/piloto FIAT, #94) — sem desenho de `@lp-architect` ainda; é o mais
   distante de começar.
6. **Validação `@lp-contract-qa`** para os Slices 1-3 já mesclados, antes de declarar qualquer um
   deles "pronto para consumo" pelo `LayoutParserReact`.
7. **README** — nenhum dos 3 slices concluídos documentou a mudança; acumula dívida de
   documentação a cada slice que passa sem isso.
