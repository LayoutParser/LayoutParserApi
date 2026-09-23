# Resposta a gaps do front-end (LayoutParserReact) — 2026-09-05

Investigação para as issues do front #188/#177/#178/#206 (Pergunta 1) e #200/#205 (Pergunta 2).
Nenhum código de produção foi alterado — apenas investigação (`@lp-architect`, escopo `review-arch`).

## Pergunta 1 — `POST /api/parse/auto` já está em produção

**Resposta objetiva: SIM, já foi promovido. Não há bloqueio real remanescente — só falta atualizar
o board (issue #188 e PR #189 do front, que ainda mostram checkbox pendente).**

Evidência:

- O commit que introduziu `ParseController.Auto`/`IAutomaticLayoutDetectionService`
  (`565d8f5`, "feat(parsing): detectar layout automaticamente por documento (#222)",
  PR #222 **MERGED**) é ancestral de `origin/master`
  (`git merge-base --is-ancestor 565d8f5 origin/master` → true).
- O merge `develop → master` que levou esse commit para produção foi o **PR #233** ("Develop"),
  mergeado em `2026-08-31T17:11:20Z`.
- `git show origin/master:Controllers/ParseController.cs` confirma hoje, em `master`:
  `[HttpPost("auto")]` (linha 740) e `[HttpPost("detect")]` (linha 590, alias que chama o mesmo
  método — comentário na linha 102 do arquivo).
- Deploys de produção (`Deploy API to Production Server`, workflow `deploy.yml`) rodaram com
  sucesso após esse merge, incluindo o mais recente em `2026-09-05T10:18:06Z` (PR #306,
  `develop → master`, mergeado hoje).
- Não há PR aberto pendente em `LayoutParser/LayoutParserApi` (`gh pr list --state open` vazio) —
  nada travado esperando aprovação.

**Achado relevante sobre o front:** o front (`LayoutParserReact`) já promoveu o consumo desse
endpoint — **PR #189** ("release: promover detecção automática de layout", `develop → main`) foi
mergeado em `2026-08-31T17:11:42Z`, **22 segundos depois** do merge da API (#233 às 17:11:20Z).
A ordem foi respeitada (API antes do front), mas por uma margem mínima — não há evidência de que
alguém tenha conferido o status da API antes de mesclar o PR do front; foi coincidência de timing
ou alguém agiu rápido demais em sequência. Vale investigar se houve checagem manual real antes do
merge do #189, porque o texto do próprio PR diz "Promover este PR antes da API quebraria o upload
em produção" e lista "LayoutParserApi develop → master promovida" como item **não marcado** —
ou seja, o item de bloqueio ficou sem check mesmo tendo sido satisfeito.

**Ação recomendada para `@lp-pm`:** fechar/atualizar a issue #188 e destravar qualquer epic/PBI
dependente (#177/#178), pois o gate está tecnicamente liberado desde 2026-08-31.

## Pergunta 2 — evidência de isolamento de workspace e read-only do Sysmiddle no servidor

### 2.1 Isolamento entre workspaces

Teste citável que prova que usuário de um workspace não lê recurso de outro:

- **Arquivo:** `tests/LayoutParserApi.Tests/Controllers/WorkspacesControllerTests.cs`
- **Teste:** `GetWorkspace_usuario_B_nao_le_workspace_de_usuario_A` (linha 144) — cria workspace
  pertencente ao usuário A, monta o controller autenticado como usuário B (sem membership no
  workspace de A) e afirma `Assert.IsType<NotFoundResult>(result)`.
- **Teste complementar (anti-enumeração):** `GetWorkspace_inexistente_e_nao_membro_respondem_o_mesmo_404`
  (linha 167) — prova que "workspace não existe" e "workspace existe mas não é meu" retornam
  exatamente o mesmo `404`, sem vazamento de informação por diferença de status/corpo.
- Comentário no código (linha ~140) confirma a query real (`WHERE m.UserId = @UserId`) é o que
  torna esse teste vermelho se a filtragem por membership for removida — não é um teste "solto",
  está acoplado à implementação real da consulta.
- `tests/LayoutParserApi.Tests/Controllers/FiscalMappingPackagesControllerTests.cs` (comentário
  de cabeçalho, linha 12) referencia explicitamente esses testes de `WorkspacesControllerTests`
  como a prova de que "usuário de outro workspace nunca lê o pacote alheio" — ou seja, o padrão de
  isolamento é compartilhado entre os controllers fiscais, não reimplementado ad-hoc em cada um.

**Nota:** `IdentityWorkspaceServiceTests.cs` (mencionado na pergunta original) cobre concorrência
na criação/resolução de usuário e workspace pessoal — não é o teste que prova isolamento
cross-workspace. O teste correto a citar para essa alegação é `WorkspacesControllerTests`, acima.

### 2.2 Sysmiddle é read-only — nenhuma rota de mutação aceita `engine=sysmiddle`

- **`tests/LayoutParserApi.Tests/Security/SysmiddleGateTests.cs`** — 15 `[Fact]`/`[Theory]`
  (algumas `[Theory]` com múltiplos `InlineData`, o que explica a citação de "25 testes" vinda do
  form do front — é o total de casos executados, não de métodos). Cobre, entre outros:
  - `Controllers_fiscais_aplicam_MappingEngineGuardFilter_no_nivel_da_classe` — todo controller
    fiscal tem o filtro aplicado na classe, não em endpoints avulsos.
  - `Engine_sysmiddle_em_qualquer_variacao_de_casing_e_recusado_no_query` (Theory, várias
    variações de caixa) e `Engine_sysmiddle_com_espacos_ao_redor_e_recusado`.
  - `Engine_sysmiddle_como_string_no_body_e_recusado`, `_como_array_no_body_e_recusado`,
    `_como_objeto_no_body_e_recusado_failclosed` — cobre os 3 formatos JSON possíveis do campo,
    todos fail-closed.
  - `Query_e_body_divergentes_sysmiddle_em_qualquer_um_basta_para_recusar` — não dá para
    contornar mandando `engine=xslt` na query e `sysmiddle` no body (ou vice-versa).
  - `MappingExplanationController_mesmo_permitindo_sysmiddle_nunca_habilita_Author` — mesmo no
    único controller que deliberadamente não aplica o filtro (leitura de explicação), garante que
    a capacidade de escrita/autoria nunca é habilitada.
  - `Nenhum_tipo_carregado_na_API_parece_um_writer_ou_serializer_Sysmiddle` e
    `ExecuteSysmiddleCandidatesAsync_e_privado_e_nao_expoe_metodo_publico_de_escrita` — testes de
    reflection sobre os assemblies carregados, não apenas sobre HTTP: garantem que não existe
    *nenhum* caminho de código (nem indireto) para escrita Sysmiddle, não só que a rota HTTP nega.

- **`tests/LayoutParserApi.Tests/Filters/MappingEngineGuardFilterTests.cs`** — 8 testes, unitários
  sobre o filtro isoladamente (sem precisar dos controllers reais): variação de casing, engine
  ausente (não bloqueado — decisão correta, é responsabilidade de outro controller recusar
  ausência), `sysmiddle` no body mesmo com engine válido na query, formato array e objeto JSON.

**Conclusão citável para o front:** as duas preocupações (#200, #205) têm cobertura de teste real
e específica no servidor, não apenas "existe um filtro registrado" — os testes cobrem os vetores
de bypass mais óbvios (casing, espaços, JSON array/objeto, divergência query vs. body, e até
verificação por reflection de que não existe writer Sysmiddle carregado no processo).

## Arquivos citados nesta investigação

- `Controllers/ParseController.cs` (origin/master, linhas 590, 740, 750, 784)
- `tests/LayoutParserApi.Tests/Controllers/WorkspacesControllerTests.cs` (linhas 144, 167)
- `tests/LayoutParserApi.Tests/Security/SysmiddleGateTests.cs`
- `tests/LayoutParserApi.Tests/Filters/MappingEngineGuardFilterTests.cs`
- `tests/LayoutParserApi.Tests/Controllers/FiscalMappingPackagesControllerTests.cs` (linha 12,
  comentário)
