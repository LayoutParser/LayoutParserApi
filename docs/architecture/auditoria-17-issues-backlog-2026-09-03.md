# Auditoria de 17 issues OPEN vs. estado real do código — 2026-09-03

Metodologia: cada issue foi lida por completo (`gh issue view`, incluindo comentários), e o
critério de aceite foi confrontado com o código em `origin/develop` (branch de trabalho
`audit/17-issues-backlog-2026-09-03`, criada a partir de `origin/develop`, HEAD
`61c302f` no momento da auditoria) — grep/read direto nos arquivos citados, não inferência.
Nenhuma issue foi fechada por este agente.

---

## #88 — tech-debt: 27 achados de alta severidade do SecurityCodeScan aceitos em baseline

**Veredito: AINDA ABERTA / NÃO INICIADA.**

`security-code-scan-baseline.json` hoje tem **32 achados**, não 27 — o baseline **cresceu**
desde que a issue foi escrita (novo `SCS0016` em `Controllers/FiscalMappingPackagesController.cs:48`,
`SCS0016` extra em `ParseController.cs:84`, linhas deslocadas nos demais arquivos). Nenhum dos
achados foi corrigido/removido do baseline; o critério de aceite ("zerar o baseline", tratar
`Controllers/` primeiro) não tem nenhuma evidência de progresso.

---

## #90 — tech-debt: capacidade pode ser registrada e nunca ativada (Program.cs sem gate de DI)

**Veredito: AINDA ABERTA / NÃO INICIADA.**

`tests/LayoutParserApi.Tests/Controllers/DataGenerationControllerDiTests.cs` continua sendo o
único teste de "gate de DI", e seu próprio comentário (linha 31) ainda documenta o trade-off
original: "descartamos `WebApplicationFactory<Program>`". Não existe `appsettings.Testing.json`
no repo, nem nenhum teste que carregue a composição real de `Program.cs`. O escopo (só o grupo
`Generation` coberto) não mudou.

---

## #96 — tech-debt: confirmar se FindXslFile usa sourceType/targetType na busca real

**Veredito: RESOLVIDA.**

`Services/XmlAnalysis/TransformationPipelineService.cs:424` — `FindXslFile` teve os parâmetros
`sourceType`/`targetType` **removidos por completo** da assinatura (agora só
`FindXslFile(string layoutName = null)`), com um comentário XML-doc explícito (linhas 418-422)
citando a issue #96 e confirmando que eles nunca influenciaram a busca real — o padrão usa
apenas `layoutName`. Os dois call-sites já logam o erro por conta própria antes de chamar o
método. Critério de aceite atendido pela via "confirmado que não afeta resolução hoje → parâmetros
mortos removidos", não pela via "passam a ser usados" — mas é uma resposta válida e completa ao
que a issue pedia (confirmar com evidência).

---

## #97 — story: IA segregada por sessão de usuário (fase 2)

**Veredito: PARCIALMENTE RESOLVIDA** — provavelmente substituída na prática pela issue #102 (CLOSED).

`Services/Database/SqlAiUserSessionStore.cs` implementa exatamente o agregado que #97 pedia:
tabela `tbLpAiUserSession` com `CustomPromptInstruction` (prompt customizado ativo) +
`tbLpAiUserSessionHistoryEntry` (histórico de tickets/status por usuário), chaveado por
`ICurrentUser.Name` (o mesmo padrão do particionamento básico da #92, como #97 recomendava).
Isso foi entregue via a issue #102 ("story: tabela SQL AiUserSession/AiUserSessionHistoryEntry"),
que está **CLOSED**. Falta confirmar/checar: (a) se há TTL/retenção configurado nessa tabela
(não visto no arquivo lido — parece manter histórico indefinidamente, ao contrário do que #97
sugeria "no mesmo espírito do TTL da #51"); (b) "preferências" além do prompt customizado não
aparecem no schema. Recomendação: `@lp-pm` decidir se #97 deve ser fechada como duplicata/
superada por #102, ou mantida aberta só para o gap de TTL/retenção.

---

## #103 — story: autoria fiscal assistida a partir de amostras + Excel + XSD

**Veredito: RESOLVIDA (nível de rastreamento).**

As 3 sub-issues formalmente vinculadas — #229 (pacote fiscal versionado), #230 (MappingDraft
human-in-the-loop), #231 (compilação TCL/XSL/XSLT + Fiscal Test Lab) — estão todas **CLOSED**
(`3/3 sub-issues-completed` já aparece no próprio `gh issue view`). Não foi verificado código
linha a linha de cada sub-issue individualmente (fora do escopo desta leva — são issues próprias),
mas a issue-guarda-chuva #103 não tem mais trabalho pendente sob ela. Recomendação: `@lp-pm`
fechar #103 como consequência do fechamento das 3 filhas, a menos que haja algum critério do corpo
da própria #103 que nenhuma das 3 sub-issues cobria (não identificado nesta auditoria).

---

## #104 — tech-debt: teste ponta a ponta faltando para TryEnqueueAiCandidate (double x86)

**Veredito: AINDA ABERTA / NÃO INICIADA.**

`tests/LayoutParserApi.Tests/Controllers/TransformationExecutionControllerUserIsolationTests.cs`
continua usando `System.Reflection` para ler `CurrentUserId` (linhas 1, 153-187) — exatamente a
técnica que a issue descreve como limitação conhecida. Nenhum double/fake do runner x86 foi
encontrado no repo.

---

## #108 — investigação: drift completo appsettings.json produção vs. repo

**Veredito: RESOLVIDA** (como investigação — a issue pede decisão/registro, não implementação).

O comentário de `@lp-architect` na própria issue (2026-08-2x, referenciado por #110/#112) entrega
os 3 itens do critério de aceite: (1) mecanismo de detecção avaliado (A1 validação de startup +
A2 health check, recomendação combinada A1 restrito + A2); (2) mecanismo de sincronização avaliado
— descobriu que já existe um step maduro "Config drift repo x destino" em `deploy.yml:462-627`,
dry-run, nunca ativado, e recomenda B1 (ler o dry-run) → B2 (ativar `MIGRATE_CONFIG_TO_REPO=true`);
(3) itens de implementação decorrentes foram desdobrados em issues novas — #110 (B1), #111 (A2,
**CLOSED**), #112 (B2). O único item do critério de aceite ainda pendente é a execução real do
B1/B2 pelo `@lp-devops`, que é o escopo das próprias issues-filhas #110/#112, não desta investigação.

---

## #110 — chore: rodar dry-run "config drift" contra produção e reportar (B1)

**Veredito: AINDA ABERTA / NÃO INICIADA.**

O step "Config drift repo x destino (dry-run por padrão)" existe em `deploy.yml:462-627` e
está pronto para rodar, mas não há nenhum relatório/comentário na issue nem em
`docs/architecture/` com o resultado de uma execução real contra produção. É uma ação
operacional (`@lp-devops` disparando o workflow e lendo o log do Actions) que não deixa rastro
no código — não há como confirmar via git que já rodou.

---

## #112 — chore: ativar MIGRATE_CONFIG_TO_REPO=true no deploy.yml (B2)

**Veredito: BLOQUEADA** — depende de #110 (B1), que ainda está aberta.

O guard-rail de segredo já está implementado no step (`deploy.yml`, regex
`password|apikey|secret|token|connectionstring`), mas a variable
`vars.MIGRATE_CONFIG_TO_REPO` não foi encontrada ativada em nenhum lugar do repo — só referenciada
como leitura condicional (`deploy.yml:492`). Consistente com o critério de aceite da própria
issue, que exige ler o relatório do B1 antes.

---

## #137 — story: mapeamento campo TXT ↔ tag XML — guarda-chuva de execução

**Veredito: RESOLVIDA (nível de rastreamento).**

As 4 sub-issues explicitamente listadas no plano — #138 (Fase 0, sectionMappings), #139 (Fase 1,
shape do MapperVO), #140 (Fase 2, catálogo GUID→XPath), #141 (Fase 3, fieldMappings em
execute-candidates) — estão todas **CLOSED**. Confirmado em código: `fieldMappings` (#141) está
implementado e em uso em `Controllers/TransformationExecutionController.cs` (`TryComposeFieldMappings`,
linha 504, com comentários citando #141 nas linhas 471/478/480/576). Recomendação: `@lp-pm` fechar
#137 como guarda-chuva concluído.

---

## #151 — investigação: reconstrução reversa best-effort XML→TXT (Fase 4)

**Veredito: AINDA ABERTA / NÃO INICIADA** — mas o bloqueio original **foi removido**.

A issue está formalmente "Bloqueada por #139 e #140" no corpo — ambas estão agora **CLOSED**
(confirmado acima, na auditoria de #137), então a issue deixou de estar bloqueada e pode ser
puxada para priorização. Porém não há nenhuma evidência de trabalho: nenhum `Reversible`/`Direction`
(Forward|Reverse) encontrado em `ai/` ou nos controllers. Recomendação: `@lp-pm` remover o rótulo
de bloqueio (se houver) e mover para o backlog priorizável.

---

## #173 — tech-debt: TransformationValidatorService sem validação detalhada (TODO)

**Veredito: AINDA ABERTA / NÃO INICIADA.**

`Services/Transformation/TransformationValidatorService.cs:302` ainda tem
`// TODO: Implementar validação mais detalhada` — texto idêntico ao citado na issue (a linha
mudou de 201 para 302 por deslocamento de código, não por edição do TODO em si). Nenhuma
definição de escopo documentada, nenhuma implementação.

---

## #196 — bug: colapso posicional em LINHA006 do layout .mqseries

**Veredito: BLOQUEADA** — no dono do projeto, exatamente como a própria issue já registra.

O critério de aceite depende de um `correlationId` de um parse real reproduzindo o bug, que só
o dono pode fornecer. Nenhuma mudança de código relacionada a `ParseLineFields`/`LengthField`
em `Services/Implementations/LayoutParserService.cs` foi encontrada além do que já estava
descrito na própria issue como hipótese não confirmada. Nada a fazer até o dono entregar o
`correlationId`.

---

## #216 — story: expor detect_layout no MCP após estabilizar o contrato

**Veredito: BLOQUEADA** — depende de #213 (issue pai), não verificada nesta auditoria (fora da
lista das 17). Não há tool `detect_layout` no MCP Server (`mcp/LayoutParserMcp/`) — consistente
com "não iniciar antes da aprovação do contrato de #213". Recomendação: se uma auditoria futura
cobrir #213, revisitar esta.

---

## #218 / #219 / #221 — epic + 2 gates: autenticação M2M e layout FIAT (E2E Cypress)

**Não são satisfeitas, nem total nem parcialmente, pelo trabalho da "plataforma fiscal" (Slice 1
identidade/workspace).** São duas frentes tecnicamente distintas do que a plataforma fiscal
resolveu. Achado importante: a causa raiz de #218 ficou mais clara nesta auditoria — o commit
`9a09194` ("feat(seguranca): habilita [Authorize(Roles=...)] nos endpoints privilegiados (issue
#32)") adicionou `[Authorize(Roles=...)]`/`[Authorize]` a `DataGenerationController`,
`LogsController`, `MapperDatabaseController` e boa parte de `TransformationExecutionController`
(incluindo os 3 endpoints citados no epic: `generate-for-layout` não tem `[Authorize]`, mas
`execute`/`execute-lowcode`, via `TransformationExecutionController`, têm `[Authorize]` em vários
métodos). Isso é o mecanismo que produz o 401 relatado no Cypress — RBAC (issue #32, já mergeada)
depende da identidade injetada pelo BFF (`TrustedIdentityMiddleware`), e um cliente Cypress
batendo direto na API sem passar pelo BFF não tem essa identidade. **Nenhum mecanismo de
autenticação machine-to-machine (client credentials, API key de serviço, mTLS, service account)
foi encontrado no código** — a única menção a "M2M" no repo é em
`docs/architecture/plano-tecnico-backlog-pendente-2026-09-02.md` (plano, não implementação).

### #218 — gate: autenticação M2M

**Veredito: AINDA ABERTA / NÃO INICIADA.** Nenhuma decisão de arquitetura registrada
(nenhum ADR/doc encontrado especificamente sobre o mecanismo de auth de serviço), nenhum código.

### #219 — gate: generate-for-layout recusa layoutType=2

**Veredito: AINDA ABERTA / NÃO INICIADA — mas com diagnóstico avançado nesta auditoria** (não
estava confirmado antes, agora está). `Services/XmlAnalysis/AutoTransformationGeneratorService.cs:153-164`
compara `layout.LayoutType` (string) literalmente contra `"TextPositional"` ou `"XML"`; qualquer
outro valor cai no warning "Tipo de layout não suportado". `Services/Database/LayoutDatabaseService.cs:129/245`
lê `LayoutType` diretamente da coluna SQL `[LayoutType]` como string crua, sem nenhum mapeamento
de código numérico → nome do enum. Isso confirma, com evidência de código (não apenas hipótese),
a hipótese (ii) do dono: **é um problema de cadastro/mapeamento** — a coluna do banco para o
layout FIAT (`ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c`) provavelmente guarda o literal `"2"` em vez
de `"TextPositional"`, e não há nenhuma tradução de código numérico legado do Sysmiddle para o
enum de string que este código espera. Isto ainda não é 100% conclusivo sem consultar o dado real
no banco (fora do alcance desta auditoria de código), mas reduz fortemente a incerteza registrada
na issue original ("Nenhuma das duas hipóteses foi validada"). Repassar este achado para
`@lp-parser-llm` como ponto de partida concreto.

### #221 — epic (Frente A + Frente B)

**Veredito: AINDA ABERTA / NÃO INICIADA**, herda o estado das duas filhas. Nenhuma ADR de
autenticação M2M registrada em `docs/architecture/`, nenhuma decisão "(i) suportar layoutType=2"
vs "(ii) corrigir cadastro" documentada ainda — apesar de esta auditoria já ter avançado
consideravelmente a evidência para a opção (ii) do lado do código (ver #219 acima).

---

## Achado incidental (fora do escopo das 17, mas relevante)

`.claude/rules/security.md` (checked into the repo) ainda afirma "Nenhum endpoint tem
`[Authorize]`/enforcement por papel ainda — é decisão de produto em aberto" — isso está
**desatualizado**: a issue #32 (mergeada, commit `9a09194` + follow-ups `55a5b15`/`948bb29`) já
adicionou `[Authorize(Roles=...)]`/`[Authorize]` a vários controllers sensíveis. Recomenda-se
sinalizar a `@lp-doc` para atualizar `security.md` — é exatamente a causa raiz visível de #218.

---

## Resumo tabular (17 issues, sem dupla contagem)

| Categoria | Issues | Contagem |
|---|---|---|
| **RESOLVIDA** | #96, #103, #108, #137 | 4 |
| **PARCIALMENTE RESOLVIDA** | #97 | 1 |
| **AINDA ABERTA / NÃO INICIADA** | #88, #90, #104, #110, #151, #173, #218, #219, #221 | 9 |
| **BLOQUEADA** | #112, #196, #216 | 3 |

Total: 4 + 1 + 9 + 3 = **17**. ✓
