# Escopo genérico TXT/XML e acesso por papel — desenho

> **PT-BR** · `@lp-architect` (Aria), 2026-08-14. Direção de produto do dono, disparada pela
> investigação de um 403 real em `execute-candidates` (issue #32 funcionando como desenhado, mas
> escopo demais). Este documento é **desenho**, não implementação — execução por
> `@lp-backend-dev`/`@lp-parser-llm` após aprovação do dono. Ver §7 (RBAC), §8 (TXT/XML genérico),
> §9 (fronteira XSLT), §10 (login — fora de escopo deste repo, só sinalizado).

---

## 0. O que o dono pediu (resumo executivo)

Todo usuário autenticado (não só `admin`) deve poder: (1) parsear documentos — já funciona; (2)
**ver** a transformação (Sysmiddle e TCL/XSL/XSLT) de um arquivo — hoje bloqueado por
`[Authorize(Roles="admin")]` em `execute-candidates`; (3) fazer login com qualquer conta
Google/Microsoft, pessoal ou corporativa — fora deste repo, só sinalizado; (4) alterar informações
do documento em TXT **ou XML**, e a ferramenta **não pode ficar nichada em TXT** — a meta declarada
é transformação **XML→XML genérica**, usando XSLT como mecanismo, não uma peça acoplada ao fluxo
TXT.

---

## 1. Investigação — o que já existe (não presumir, o código foi lido)

### 1.1 `[Authorize]` hoje (todos checados nesta sessão)

| Controller | Endpoint | Papel | O que faz |
|---|---|---|---|
| `TransformationExecutionController` | `POST execute-candidates` | `admin` | Gera candidatos multi-pathway (sysmiddle + tcl-xsl) — **é o endpoint do 403 relatado** |
| `TransformationExecutionController` | `GET execute-candidates/{ticket}/ia-status` | `admin` | Consulta status do job assíncrono de IA (issue #40) |
| `TransformationExecutionController` | `POST execute-lowcode` | `admin` | Roda o motor Sysmiddle via runner `.exe` diretamente |
| `DataGenerationController` | `POST generate-synthetic`, `process-excel`, `generate-synthetic-zip` | `admin` | Gera dado sintético / roda IA em volume |
| `LogsController` | `GET` (listagem) | `admin` | Expõe internals (stack traces, paths de servidor) |
| `MapperDatabaseController` | `POST refresh-cache` | `operador` | Invalida cache de mapeadores |

Nenhum outro endpoint tem `[Authorize]`. `POST /api/parse/upload` continua deliberadamente aberto
(decisão já registrada em `rollout-p2-autenticacao.md`).

### 1.2 Suporte real a XML→XML — já existe, não é aspiracional

Achado central: **o pathway XML→XML já é código real, não um parâmetro decorativo.**

- `TransformationRequest.InputContent` é inspecionado (`TrimStart().StartsWith("<")`) tanto em
  `execute` quanto em `execute-candidates` (`TransformationExecutionController.cs:88,201`) — a
  detecção de tipo de entrada é automática, o cliente não precisa declarar.
- `LowCodeTransformationEligibility.Evaluate` (gate central do pathway Sysmiddle) recebe `isXmlInput`
  como parâmetro de primeira classe e **bloqueia explicitamente** entrada XML do pathway low-code
  (`TypeNotPositionalReason`) — decisão deliberada, documentada: "Sysmiddle/low-code espera texto
  posicional (TXT), não XML" (`TransformationPipelineService.cs:293`).
- `AiCandidateDispatchPlan.TryBuild` também recebe `isXmlInput` e retorna `null` se verdadeiro — o
  pathway de IA (issue #40) também está gateado para não tentar aprender de XML como se fosse TXT.
- `TransformationPipelineService.TransformXmlToXmlAsync(xmlContent, sourceDocumentType,
  targetDocumentType, layoutName)` **existe e é chamado** em ambos os endpoints quando
  `isXmlInput == true` — carrega um XSL via `FindXslFile` (convenção `*_{layoutName}.xsl`) e aplica
  via `XslCompiledTransform` real (`.NET` nativo, não um placeholder).
- `Models/TransformationRequest.cs` já tem `SourceDocumentType`/`TargetDocumentType` como campos do
  contrato — o payload já aceita a direção da transformação.

**Conclusão da investigação:** a infraestrutura de pipeline **já é agnóstica TXT/XML** no nível de
`TransformationExecutionController`/`TransformationPipelineService`. O que falta não é "construir o
pathway XML→XML do zero" — é (a) abrir o acesso (RBAC, §7), (b) fechar lacunas de robustez do
pathway XML→XML que hoje são mais frágeis que o TXT→XML por terem menos uso/teste (§8.2), e (c)
decidir se "genérico" também deve cobrir *edição* do XML de entrada, não só transformação (§8.3).

---

## 2. Tabela de enforcement por papel — revisão

Princípio: **visualizar/consultar candidatos de transformação (qualquer pathway, incluindo IA) é
uso normal do produto e deve ser aberto a qualquer usuário autenticado** — é o requisito de produto
(2) do dono. Continua restrito a `admin` o que é **destrutivo, caro em recursos compartilhados, ou
vaza internals do servidor** — nada disso mudou de natureza, só a lista de quem cai em cada balde.

| Endpoint | Papel hoje | Papel proposto | Justificativa da mudança (ou da permanência) |
|---|---|---|---|
| `POST /api/TransformationExecution/execute-candidates` | `admin` | **qualquer usuário autenticado** | É o requisito de produto (2) do dono. Gera candidatos e os retorna — **não altera estado persistente**, só roda os runners e devolve o resultado. O risco original do issue #32 ("dispara processo externo x86") continua real, mas é o **mesmo custo computacional que qualquer usuário do produto deveria poder pagar** para ver a transformação do próprio arquivo — não é uma operação de administração de sistema. Ver ressalva de custo/DoS abaixo. |
| `GET /api/TransformationExecution/execute-candidates/{ticket}/ia-status` | `admin` | **qualquer usuário autenticado** | Consulta — mesmo raciocínio de `execute-candidates`, do qual é o companheiro assíncrono. Fechar um e abrir o outro seria inconsistente. |
| `POST /api/TransformationExecution/execute-lowcode` | `admin` | **qualquer usuário autenticado** | Mesma classe de `execute-candidates` (roda o motor Sysmiddle via runner) — é o caminho direto de "ver a transformação Sysmiddle" que o dono pediu explicitamente no item (2). |
| `POST /api/DataGeneration/generate-synthetic`, `process-excel`, `generate-synthetic-zip` | `admin` | **mantém `admin`** | Não é "ver a transformação de um documento" — é geração de dado sintético em volume / ingestão de Excel para criar novos layouts. Continua sendo operação de curadoria de catálogo, não consulta. Fechar isso reabriria exatamente o problema original que motivou o RBAC: superfície de abuso computacional sem fricção. |
| `GET /api/Logs` | `admin` | **mantém `admin`** | Vaza stack traces e paths de servidor — risco de informação, não de custo. Nenhuma leitura do pedido do dono cobre "ver logs internos"; é operação de suporte/operação, não de uso do produto. |
| `POST /api/MapperDatabase/refresh-cache` | `operador` | **mantém `operador`** | Ação de manutenção de cache compartilhado entre todos os usuários — um usuário comum invalidando o cache de todo mundo é o tipo de ação que precisa de um papel intermediário, não `admin` nem "qualquer usuário". Não está na lista do pedido do dono. |
| `POST /api/parse/upload` | (nenhum) | **mantém aberto** | Já decidido em `rollout-p2-autenticacao.md`; item (1) do pedido do dono confirma que deve continuar assim. |

### 2.1 Trade-off explícito: abrir `execute-candidates`/`execute-lowcode`/`ia-status`

- **Opção A — abrir para qualquer usuário autenticado (recomendado, é o que o dono pediu).**
  - Prós: atende ao requisito de produto sem ambiguidade; consistente com "parsear já é aberto" —
    ver a transformação é a continuação natural do mesmo fluxo.
  - Contras: reintroduz parcialmente o problema original do issue #32 — **qualquer usuário
    autenticado** (não só quem tem crachá de admin) pode disparar até `MaxConcurrentRunners`
    processos x86 simultâneos por chamada. Antes, isso era restrito a um punhado de admins;
    agora é qualquer conta que passar pelo login (que, pelo pedido (3), pode ser **qualquer conta
    Google/Microsoft pessoal** — ver §10). O RBAC deixa de ser a defesa contra abuso de recurso
    computacional; **precisa de outra camada** (rate limit por usuário, fila com prioridade,
    quota) se o volume de usuários crescer. Isso não está implementado hoje — é dívida
    identificada, não silenciada.
- **Opção B — criar um papel intermediário (ex.: `usuario`) só para os 3 endpoints, mantendo
  fricção de RBAC sem taxonomia binária admin/não-admin.**
  - Prós: preserva algum controle de quem pode disparar (ex.: só contas corporativas), sem
    depender de rate limit ainda não construído.
  - Contras: contradiz o pedido literal do dono ("TODO USUÁRIO... não só admin"); se o login (3)
    aceita conta pessoal sem processo de aprovação, criar um papel intermediário exige um
    mecanismo de atribuição de papel que também não existe hoje — mais trabalho para o mesmo
    resultado que o dono já pediu como "todo mundo".

**Recomendação: Opção A**, porque é o que foi pedido de forma inequívoca, **com a ressalva
registrada** de que o controle de custo computacional (rate limit/quota por usuário) vira uma
lacuna nova a fechar — sinalizo aqui, não decido a prioridade dela. Anotar como item de backlog
separado (`@lp-pm`), não bloquear esta mudança de RBAC por ela.

---

## 3. Arquitetura para suporte genérico TXT→XML e XML→XML

### 3.1 O que já é agnóstico (não precisa mudar)

- Detecção de tipo de entrada (`isXmlInput`) já é automática e client-agnostic.
- `TransformationPipelineService` já expõe os dois métodos (`TransformTxtToXmlAsync`/
  `TransformXmlToXmlAsync`) atrás da mesma interface pública, já ramificados corretamente nos dois
  endpoints principais (`execute`, `execute-candidates`).
- O contrato de request (`TransformationRequest`) já tem os campos necessários
  (`SourceDocumentType`/`TargetDocumentType`) para XML→XML.

### 3.2 O que precisa de decisão/trabalho (gap real, não "falta implementar do zero")

1. **Descoberta de XSL para XML→XML é mais frágil que o pathway TXT.** `FindXslFile` depende de
   convenção de nome de arquivo (`*_{layoutName}.xsl`) e resolve ambiguidade "escolhendo em ordem
   alfabética" com um warning — aceitável como fallback do pathway TXT (onde o low-code/sysmiddle é
   o candidato "de verdade" e o tcl-xsl é o secundário), mas se XML→XML vira **primeira classe** do
   produto, essa heurística de ambiguidade silenciosa-com-warning precisa ser revisitada: qual XSL
   usar quando múltiplos casam pode não ter uma resposta correta sem o `SourceDocumentType`/
   `TargetDocumentType` entrarem no critério de busca (hoje `FindXslFile` recebe esses parâmetros
   mas a implementação efetivamente decide só pelo `layoutName`/padrão de arquivo — checar se
   `sourceType`/`targetType` são de fato usados na busca ou só logados, antes de expandir uso).
2. **`execute-candidates` já roda os dois pathways para XML** (via `ExecuteTclXslCandidatesAsync`),
   mas `ExecuteSysmiddleCandidatesAsync` retorna vazio para XML por design (§1.2) — então hoje, para
   entrada XML, `execute-candidates` **sempre devolve no máximo 1 candidato** (o tcl-xsl). Isso é
   coerente com "Sysmiddle é motor de TXT posicional", mas se o produto quer múltiplos candidatos
   também para XML→XML no futuro, é um segundo mecanismo de geração de candidato tcl-xsl que **não
   existe ainda** (hoje o pipeline produz 1 XSL determinístico por par source/target, não múltiplas
   variações plausíveis).
3. **Edição do documento (item 4 do pedido) é uma capacidade distinta de "ver a transformação".**
   O pedido do dono mistura duas coisas: (a) executar/visualizar a transformação de um documento
   existente — já coberto por `execute`/`execute-candidates`; (b) "alterar informações do documento"
   — isso soa a **editar o conteúdo de entrada (TXT ou XML) antes ou depois de transformar**, o que
   é uma feature de edição, não de transformação. Não há hoje nenhum endpoint de edição de
   documento — só parse (leitura) e transformação (geração de novo XML a partir do original). Se
   "alterar" significa "o usuário edita o XML final gerado e o sistema aceita essa edição como
   insumo para reprocessar/reexecutar", isso é uma feature nova (edição + re-transformação),
   fora do escopo deste desenho — precisa de confirmação do dono sobre o que "alterar" significa
   operacionalmente antes de desenhar.

### 3.3 Recomendação de sequência

1. Abrir o RBAC (§2) primeiro — é a mudança mais barata e já desbloqueia "ver a transformação" para
   todo usuário, que é a dor concreta relatada (403 real).
2. Confirmar com `@lp-parser-llm` se `FindXslFile` de fato usa `sourceType`/`targetType` na busca
   (ler `Directory.GetFiles` completo) ou só no log — se só loga, é um gap de correção a fechar
   antes de declarar XML→XML "pronto para produção" com múltiplos formatos de documento simultâneos.
3. Escalar ao dono a pergunta de §3.2.3 (o que "alterar informações do documento" significa) antes
   de desenhar qualquer endpoint de edição — não assumir.

---

## 4. Fronteira honesta do XSLT (reaproveitando `viabilidade-dlls-sysmiddle-para-rag.md` §5)

Já mapeado nesta análise anterior — reafirmado aqui porque é diretamente relevante para "genérico
XML→XML": XSLT **não resolve tudo**, e forçar o que ele não expressa bem em XSLT reintroduz
exatamente a complexidade que o projeto está tentando eliminar (dependência do runtime Sysmiddle).

| Categoria | XSLT cobre? | Implicação para o escopo genérico |
|---|---|---|
| Condicional, formatação, cálculo simples, concatenação, truncamento, lookup em tabela estática | Sim, nativamente/`xsl:key` | É a maior parte de qualquer transformação XML→XML fiscal — cobre o núcleo do requisito |
| Chamada a serviço externo (HTTP, SQL em tempo de transformação) | **Não** | Se alguma regra de negócio real exigir isso, a resposta correta é pré/pós-processamento em C# na API, **não** forçar em XSLT — não fingir genericidade além do que a ferramenta expressa |
| Estado mutável complexo entre elementos não-relacionados na árvore | **Fraco** | Risco concreto ao generalizar para XML→XML arbitrário: documentos com regras de dependência cruzada entre seções distantes (comuns em NFe complexa) podem não caber bem em XSLT puro |
| Geração de valor não-determinístico (GUID, timestamp real) | Parcial | Mesmo tratamento já usado no diff de validação (normalização) se aparecer no fluxo genérico |

**Consequência para este desenho:** "genérico XML→XML" deve ser lido como **"o pipeline aceita
qualquer par (SourceDocumentType, TargetDocumentType) desde que a transformação entre eles seja
expressável em XSLT"** — não como "qualquer transformação imaginável entre dois XMLs quaisquer".
Quando aparecer um caso real que XSLT não cobre bem, a resposta arquitetural é orquestração C# na
API (já é o padrão do projeto), não abandonar XSLT nem tentar forçar o inexpressável.

---

## 5. Login (item 3 do pedido) — constraint reconhecida, fora de escopo deste repo

Login via **qualquer conta Google ou Microsoft, corporativa ou pessoal** é trabalho do
`LayoutParserReact/server/` (BFF Fastify), hoje implementando Entra OIDC (branch de referência
histórica `codex/feat-entra-oidc`, conforme `rollout-p2-autenticacao.md`); Google ainda não
implementado ali. Não desenho a implementação aqui — só registro a constraint e uma pergunta.

> **Sinalização, não decisão:** aceitar **conta pessoal** (não só corporativa/tenant controlado)
> como identidade válida para acessar um sistema que processa **dado fiscal de cliente** é uma
> escolha com implicação de segurança/compliance que talvez mereça confirmação explícita do dono —
> hoje a defesa de "quem é o usuário" é inteiramente delegada ao provedor OIDC (Google/Microsoft);
> uma conta pessoal comprometida (sem MFA corporativo, sem política de senha da empresa) vira
> caminho de acesso ao mesmo sistema. Não é uma objeção — é uma pergunta a fazer ao dono antes de
> a decisão virar padrão: "dado fiscal sensível acessível por login com conta pessoal
> não-corporativa é decisão consciente, ou o requisito era só 'não travar no tenant específico da
> empresa' (ainda corporativo, só sem exigir *o* tenant específico)?"

---

## 6.1 Correção ao vivo (dono, durante a escrita deste documento) — escopo real do `admin`

O dono refinou o requisito enquanto este documento era escrito: `admin` **não é** "quem pode ver
transformação" (isso já virou acesso de qualquer usuário autenticado, §2). `admin` é
especificamente **gerenciamento dos artefatos de mapeamento já gerados** (TCL, XSL/XSLT) —
CRUD/governança: editar, aprovar, revogar, promover um candidato gerado pela IA a "oficial", etc.
Isso muda a leitura da tabela §2: não é mais "admin vê, usuário não vê" — é **"todo usuário
vê/consulta, admin gerencia/modifica os artefatos"**. Investigado abaixo o que já existe de
operação sobre mapper/candidato no código atual.

### O que já existe hoje sobre mapper/candidato (lido em `MapperDatabaseController.cs`)

| Endpoint | Operação | Natureza |
|---|---|---|
| `GET by-layout/{layoutGuid}` | Lista mapeadores de um layout | Leitura |
| `GET all` | Lista todos os mapeadores | Leitura |
| `GET export/{id}` | Exporta um mapeador como JSON completo (inclui `DecryptedContent`) | Leitura (mas expõe conteúdo descriptografado — ver ressalva abaixo) |
| `GET by-input/{inputLayoutGuid}` | Busca mapeador por layout de entrada | Leitura |
| `POST refresh-cache` | Invalida cache de mapeadores | Manutenção (já `operador`) |

**Achado central: não existe hoje nenhuma operação de escrita sobre mapeadores** (criar/editar/
excluir TCL/XSL) nem **nenhum mecanismo de "promover candidato IA a oficial"**. O pathway IA
(`AiTransformationCandidateService`) só **gera e reporta status** (`StatusConverged`/`StatusFailed`
via `AiCandidateStore`) — o candidato convergido fica disponível para consulta (`GetStatusAsync`),
mas **nada no código hoje grava esse candidato como um mapeador persistente no catálogo**. "Promover
a oficial" é uma capacidade de escrita que precisa ser desenhada, não um botão a destravar.

### Revisão da tabela de RBAC (§2) à luz do escopo real de `admin`

| Endpoint/Operação | Natureza | Papel |
|---|---|---|
| `GET by-layout`, `GET all`, `GET by-input` (`MapperDatabaseController`) | Consulta de mapeadores existentes | **Já aberto (sem `[Authorize]`) — correto, mantém.** É "ver o artefato", consistente com o requisito de produto |
| `GET export/{id}` | Consulta, mas devolve `DecryptedContent` — conteúdo sensível descriptografado do mapeador | **Reavaliar.** Hoje sem `[Authorize]`. Se `DecryptedContent` contém regra de negócio proprietária/dado sensível do cliente, isso é diferente de "ver a transformação executada" — é "ver a receita completa do mapeador". Recomendo `admin` aqui até o dono confirmar se é aceitável qualquer usuário baixar o mapeador descriptografado inteiro (achado incidental desta investigação, não pedido explicitamente — sinalizo, não decido) |
| `POST refresh-cache` | Manutenção de cache compartilhado | Mantém `operador` (já correto, é infra, não é "gerenciar o artefato de mapeamento" em si) |
| `execute-candidates`/`execute-lowcode`/`ia-status` | **Gerar e visualizar** transformação/candidato | **Qualquer usuário autenticado** (confirmado, §2) — é execução+leitura, não escrita sobre o artefato |
| *(a construir)* Editar TCL/XSL de um mapeador existente | Escrita sobre artefato | **`admin`** — é exatamente o escopo que o dono definiu |
| *(a construir)* Promover candidato IA (`StatusConverged`) a mapeador oficial do catálogo | Escrita sobre artefato / decisão de governança | **`admin`** — mesma razão; é o exemplo mais concreto citado pelo dono |
| *(a construir)* Revogar/desativar um mapeador do catálogo | Escrita destrutiva sobre artefato | **`admin`** |

### Trade-off e gap a fechar

Não existe hoje nenhum endpoint de escrita sobre mapeador — então **a tabela acima não é uma
migração de papel em endpoints existentes** (como foi §2), é a **definição do contrato de acesso
para uma feature ainda não construída**. Recomendo que `@lp-backend-dev`/`@lp-parser-llm` tratem
"editar mapeador" e "promover candidato IA a oficial" como novo trabalho de design (endpoints +
modelo de dados de "mapeador vindo de candidato IA aprovado" — hoje o catálogo de mapeadores não
distingue origem "criado pelo analista via Sysmiddle" vs. "promovido de um candidato IA"), não como
"só adicionar `[Authorize(Roles="admin")]` em algo que já existe". `GET export/{id}` é a única peça
que já existe e precisa de decisão imediata do dono (tabela acima).

---

## 7. IA segregada por sessão de usuário — estado atual e caminho incremental

### 7.1 Investigação: como o pathway IA identifica um job hoje

Lido `AiCandidateStore` e `AiTransformationCandidateService` por completo:

- O estado é um `ConcurrentDictionary<string, StoredEntry>` **global ao processo**, chaveado só
  pelo `ticket` — não há campo de usuário em `AiCandidateStatus`, `EnqueueAsync` nem `GetStatusAsync`.
  Qualquer usuário autenticado que souber (ou adivinhar) um `ticket` de outro pode consultar
  `GetStatusAsync` e ver o candidato gerado — hoje isso não vaza porque `ia-status`/
  `execute-candidates` exigem `admin` (um punhado de contas confiáveis), mas **assim que §2 abre
  esses endpoints a qualquer usuário autenticado, isso vira um vazamento real entre usuários**, não
  hipotético. O `ticket` é derivado de `LowCodeTransformationStore.BuildTicketFromContent`
  (conteúdo+layout, não usuário) — não tem entropia de sessão.
- Persistência é por arquivo `{ticket}.json` no `MLData/AiTransformationCandidates` — mesmo
  problema: nome de arquivo não carrega identidade do usuário.
- `TrustedIdentityMiddleware` já popula `ICurrentUser` (nome + papéis) em todo request que passa
  pelo loopback — é o mecanismo de identidade que já existe e que este requisito pode reaproveitar,
  **não precisa de um novo sistema de autenticação para o passo 1**.
- Não existe hoje nenhum conceito de "sessão" (histórico de interação de um usuário com a IA entre
  chamadas) — cada `EnqueueAsync` é um job avulso amarrado a um ticket de documento, sem memória do
  que o mesmo usuário pediu antes.

### 7.2 Caminho incremental (recomendado) — isolamento por usuário primeiro, sessão completa depois

**Passo 1 — isolamento por usuário (barato, resolve o vazamento entre usuários):**
- Trocar a chave do `ConcurrentDictionary`/nome de arquivo de `ticket` para `{userId}:{ticket}`
  (ou subpasta `MLData/AiTransformationCandidates/{userId}/`), usando `ICurrentUser.Name` (já
  existe) como `userId`. `EnqueueAsync`/`GetStatusAsync` passam a receber o usuário atual (via DI
  de `ICurrentUser`, já injetável em `Scoped`) e o controller já roda dentro do pipeline de
  identidade — não precisa de novo middleware.
- Efeito: um usuário não consegue mais ler o ticket/candidato de outro mesmo adivinhando o
  identificador de conteúdo. É a correção mínima necessária **antes** de abrir `ia-status` a todo
  usuário (§2) — sem isso, a abertura de RBAC cria uma regressão de confidencialidade entre
  usuários que hoje não existe (hoje só admins, que são poucos e confiáveis, veem qualquer ticket).
- Usuário anônimo (`TrustIdentityFromLoopbackOnly` falha a identidade, ou fora do loopback):
  precisa de uma decisão — tratar como um "usuário" pseudo-anônimo com sua própria partição (chave
  `anon`), aceitando que múltiplos anônimos compartilham o mesmo balde, é o degrade mais simples e
  consistente com o resto do projeto (auditoria já grava `anon` do mesmo jeito).

**Passo 2 — sessão persistente (mais trabalho, é o que abre caminho para o requisito 8, prompt
customizado):**
- Um conceito de "sessão de IA por usuário" que sobrevive a múltiplas chamadas — não só isolamento
  de dado, mas **histórico acumulado** (candidatos anteriores, prompt customizado ativo, preferências).
  Isso é um novo agregado de domínio (`AiUserSession` ou similar), não uma troca de chave no
  dicionário existente — precisa de decisão de onde persistir (SQL, já é a fonte da verdade do
  projeto — reaproveitar em vez de inventar um terceiro mecanismo de armazenamento) e de TTL/limite
  de retenção (mesmo princípio já aplicado ao `AiCandidateStore`, issue #51).
- **Não é urgência de implementar agora** (o próprio dono classificou como "objetivo a caminhar") —
  registrado aqui como direção, para que o Passo 1 já seja desenhado de um jeito que não precise ser
  jogado fora quando o Passo 2 chegar (ex.: já usar `ICurrentUser.Name` como chave de partição desde
  o Passo 1, em vez de um esquema que precise ser todo refeito).

### 7.3 Trade-off explícito

| Opção | Prós | Contras |
|---|---|---|
| **A — só isolamento por chave (`ICurrentUser.Name` no `AiCandidateStore`)** | Barato, resolve o vazamento real e urgente (criado pela abertura do RBAC em §2); não exige novo agregado de domínio | Não dá histórico entre chamadas — cada job continua avulso, só que particionado |
| **B — sessão completa desde já (agregado novo + persistência SQL)** | Já entrega a base para o requisito 8 (prompt customizado por sessão) sem retrabalho | Mais caro, atrasa a correção do vazamento de confidencialidade que é urgente **agora**, junto com a abertura do RBAC |

**Recomendação: A agora (bloqueante junto com §2), B como próxima iteração** — não abrir
`ia-status`/`execute-candidates` a todo usuário autenticado sem o Passo 1, mas não bloquear essa
abertura esperando o Passo 2 completo.

---

## 8. Prompt customizado do usuário — desenho e superfície de risco

### 8.1 Onde o prompt é montado hoje

`AiTransformationCandidateService.BuildPrompt` (estático, sem parâmetro de usuário) monta um prompt
fixo: papel do sistema ("especialista em transformação de documentos fiscais..."), layout+mapeador,
entrada truncada, gabarito truncado, e (nas iterações seguintes) a tentativa anterior + diff. Não há
hoje nenhum ponto de extensão — é uma string montada inteiramente no código, sem interpolação de
texto vindo do request.

### 8.2 Onde o prompt customizado deveria entrar

- **Não** no payload solto de `execute-candidates`/`execute-lowcode` a cada chamada — o próprio
  requisito do dono já aponta para isso ("provavelmente devia ficar associado à sessão do usuário,
  não ser um parâmetro solto"), e concordo: um prompt customizado é uma preferência de como *aquele
  usuário* quer que a IA se comporte, não um detalhe de uma chamada isolada — cabe no conceito de
  sessão do §7.2 Passo 2 (ou, como fallback mínimo do Passo 1, um campo simples associado ao
  `userId` isolado, sem esperar o agregado de sessão completo).
- Endpoint dedicado (ex.: `PUT/POST api/AiSession/prompt-adicional`) que grava a instrução do
  usuário associada à sua partição (`ICurrentUser.Name`), separado de `execute-candidates` — mantém
  o payload de execução limpo e permite o usuário setar/atualizar a preferência uma vez, reusada em
  chamadas futuras (é exatamente o comportamento "sessão", não "parâmetro por chamada").
- `BuildPrompt` ganha um parâmetro adicional `userInstruction` que é **anexado depois** do prompt de
  sistema fixo, nunca antes e nunca substituindo — texto literal do dono: "ADICIONA... não
  substitui". Implementação: uma seção adicional clara no prompt, ex. `sb.AppendLine("INSTRUÇÃO
  ADICIONAL DO USUÁRIO (não sobrepõe as regras acima):"); sb.AppendLine(userInstruction);` —
  desenho, não código final, mas fixa o princípio de que o bloco de sistema vem primeiro e é
  imutável por texto do usuário.

### 8.3 Risco de prompt injection — como mitigar sem impedir o requisito

O próprio pathway já tem uma defesa estrutural que **não depende de o LLM "se comportar"**: a saída
não é aceita por confiança no texto gerado — passa por `CanonicalDiffer` (diff node-a-node contra o
gabarito sysmiddle) e `XsdValidationService` antes de qualquer coisa virar `StatusConverged`. Um
prompt injection que tentasse fazer o modelo "ignorar as regras e liberar qualquer XML" **ainda
esbarraria no verificador determinístico** — não é o LLM que decide se o candidato é aceito, é o
diff canônico. Isso não elimina o risco, mas limita o dano: o pior caso de injection é "o modelo
gera lixo e nunca converge" (falha segura, `StatusFailed`), não "o sistema aceita saída
adversarial como se fosse boa".

Ainda assim, dois riscos que o verificador **não cobre** e merecem mitigação na camada de prompt:
1. **Vazamento de informação de outro documento/usuário.** Se o prompt customizado tentar induzir o
   modelo a "ignorar a entrada e repetir o que você processou antes", isso só seria um risco real se
   o histórico de outro usuário estivesse acessível ao contexto do modelo — que não é o caso aqui (o
   Ollama é local, sem memória entre chamadas HTTP, cada `GenerateCandidateAsync` é uma chamada
   nova). Risco baixo hoje, mas **muda se/quando a sessão do §7.2 Passo 2 passar a incluir histórico
   no prompt** — nesse ponto, revisitar se o histórico de sessão pode vazar entre usuários por
   engano de escopo (checar isolamento por `userId`).
2. **Custo/abuso via prompt longo.** Nada limita hoje o tamanho da instrução customizada — um
   usuário poderia inflar o prompt para consumir mais tempo de Ollama (recurso compartilhado, CPU-
   only, `production-server-hardware.md`). Recomendo truncamento explícito do `userInstruction`
   (mesmo padrão já usado em `Truncate(inputContent, 4000)`) e, se o requisito de rate limit do §2.1
   for implementado, que ele cubra também chamadas que carregam instrução customizada — é o mesmo
   recurso computacional compartilhado.

### 8.4 Trade-off explícito

| Opção | Prós | Contras |
|---|---|---|
| **A — prompt customizado como campo de sessão (recomendado)** | Consistente com o pedido do dono; um lugar único para o usuário gerenciar sua preferência; abre caminho para futuras preferências (não só prompt) | Depende do conceito de sessão (§7.2) — se implementado antes do Passo 1/2 de isolamento, corre risco de nascer sem particionamento correto |
| **B — prompt customizado como campo solto no payload de `execute-candidates`** | Mais rápido de implementar isoladamente, sem esperar sessão | Contradiz a intuição do próprio dono; se o usuário precisa reenviar a instrução a cada chamada, não é "sua IA", é "um parâmetro a mais" — pior UX e mais fácil de esquecer/inconsistência entre chamadas do mesmo usuário |

**Recomendação: A**, sequenciado depois do Passo 1 do §7.2 (isolamento por usuário) — não é preciso
esperar o agregado de sessão completo (Passo 2) se a implementação inicial guardar o prompt
customizado na mesma partição por `userId` já criada no Passo 1, promovendo para o agregado de
sessão completo quando ele existir.

---

## 9. Entregáveis para os próximos agentes

- `@lp-backend-dev`: mover os 3 `[Authorize(Roles="admin")]` de execução/consulta (tabela §2) para
  "qualquer usuário autenticado"; **antes disso**, implementar o Passo 1 do §7.2 (isolamento do
  `AiCandidateStore` por `ICurrentUser.Name`) — é pré-requisito, não trabalho paralelo, porque abrir
  `ia-status` sem isolamento cria vazamento entre usuários; investigar se `FindXslFile` usa
  `sourceType`/`targetType` na busca de fato (§3.2.1); decidir com o dono se `GET export/{id}`
  (`MapperDatabaseController`) precisa de `admin` (achado §6.1).
- `@lp-parser-llm`: confirmar hipótese (a)/(b) das Functions customizadas (já pendente de
  `viabilidade-dlls-sysmiddle-para-rag.md` §4); projetar o modelo de dados de "mapeador promovido de
  candidato IA" (§6.1) antes de implementar o endpoint de promoção — hoje não existe representação
  de origem do mapeador no catálogo.
- `@lp-pm`: abrir itens de backlog separados para (a) rate limit/quota por usuário em
  `execute-candidates`/`execute-lowcode` (§2.1), (b) endpoints de escrita sobre mapeador
  (editar/promover/revogar, §6.1), (c) sessão de IA por usuário com prompt customizado (§7-§8) —
  três frentes distintas, não uma só issue.
- **Dono**: responder a pergunta de §3.2.3 (o que "alterar informações do documento" significa
  operacionalmente), a pergunta de §5 (conta pessoal vs. corporativa para dado fiscal), e confirmar
  se `GET export/{id}` deve virar `admin` (§6.1, achado incidental).

---

*LayoutParser API · Escopo genérico TXT/XML + acesso por papel · `@lp-architect` · 2026-08-14*
