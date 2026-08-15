# Sessão de usuário e artefatos compartilhados — desenho complementar

> **PT-BR** · `@lp-architect` (Aria), 2026-08-14. Complementa
> [`escopo-generico-txt-xml-e-acesso-por-papel-2026-08-14.md`](escopo-generico-txt-xml-e-acesso-por-papel-2026-08-14.md)
> (branch `docs/rbac-generico-e-resposta-frontend-2026-08-14`, PR #100) — especificamente §7
> (IA segregada por sessão) e §8 (prompt customizado), que já continham o esqueleto do Passo
> 1/Passo 2 sobre o qual esta visão de mais longo prazo se apoia. Não recria aquele documento;
> refina a direção com a visão do dono sobre sessão persistente no servidor e artefatos
> compartilhados como conhecimento institucional. Desenho, não implementação — execução por
> `@lp-backend-dev`/`@lp-parser-llm` após aprovação.

---

## 0. Separando dois conceitos que o dono está tratando juntos

O dono descreveu isso como uma coisa só ("cache por usuário, tipo sessão do Claude Code/Codex/
ChatGPT, compartilhável"), mas são **duas camadas com dono, ciclo de vida e modelo de acesso
diferentes**. Tratar como uma coisa só é o erro que geraria o desenho errado — então a primeira
decisão de arquitetura aqui é a separação, não a implementação.

| | **Sessão de usuário** (histórico de trabalho) | **Artefato promovido** (conhecimento institucional) |
|---|---|---|
| O que é | Progresso/histórico de um usuário: documentos que processou, prompt customizado (§8 do doc-mãe), candidatos em andamento | TCL/XSL/XSLT **validado** (passou `CanonicalDiffer` + `XsdValidationService`) e promovido a mapeador oficial do catálogo |
| Dono | O usuário que gerou (é o trabalho *dele*, mesmo sendo ferramenta interna — ver §1) | A NDD — não pertence a quem gerou, é conhecimento reutilizável |
| Ciclo de vida | Nasce por interação, cresce, pode ser descartado/expirado | Nasce por **promoção explícita** (ação de `admin`, já mapeada em §6.1 do doc-mãe), é permanente até revogação deliberada |
| Onde já existe hoje (parcial) | `AiCandidateStore` (TTL curto, issue #51), `LowCodeTransformationStore` | `MapperDatabaseController` (catálogo de mapeadores) — mas sem representação de "promovido de candidato IA" (achado §6.1 do doc-mãe) |
| Modelo de acesso | Privado ao usuário dono (com a ressalva do §2 abaixo) | Compartilhado — qualquer usuário autenticado lê (já é o padrão hoje, `GET by-layout`/`GET all` sem `[Authorize]`) |

A pergunta do dono ("os artefatos TCL/XSL/XSLT gerados seriam possível de ser compartilhados?")
tem resposta diferente dependendo de qual dos dois ele está perguntando. **Para artefato
promovido: sim, já é esse o design implícito hoje** (§6.1 do doc-mãe já registra que o catálogo
de mapeadores é lido por qualquer usuário — a pergunta nova não muda isso, só confirma que faz
sentido). **Para o rascunho/ticket em andamento de um usuário: não por padrão** — ver §2.

---

## 1. Ferramenta interna ≠ tudo compartilhado desde o início — recomendação

### 1.1 Os dois lados do argumento

- **(i) Isolar por padrão, mesmo sendo "tudo da NDD".** Um ticket de IA em progresso (ainda
  `StatusPending`/iterando, não `StatusConverged`) é rascunho — pode ter passado por 3 tentativas
  ruins do LLM, prompt customizado específico de quem está testando, entrada de teste que não é
  documento real. Mostrar isso a outro usuário não é vazamento de segredo entre concorrentes
  (não é esse o risco), é **ruído/confusão de UX e responsabilidade**: "por que estou vendo o
  rascunho de outra pessoa, isso é meu trabalho ou dela, quem decide se é bom". Rascunho não é
  conhecimento institucional ainda — é trabalho em progresso, e trabalho em progresso tem dono
  natural (quem está iterando nele) até que alguém (admin, via promoção) decida que virou
  conhecimento compartilhável.
- **(ii) Compartilhar desde o início, já que é tudo "da empresa".** Se a visão de produto é
  "ferramenta interna, sem fronteira de propriedade entre usuários", isolar por padrão é fricção
  desnecessária — dois analistas trabalhando no mesmo layout poderiam se beneficiar de ver o
  rascunho um do outro, evitando retrabalho duplicado.

### 1.2 Recomendação: (i) — isolar por padrão, com leitura sob demanda como extensão futura

Isolamento por padrão **não é sobre confidencialidade entre usuários hostis** (não é esse o
modelo de ameaça de uma ferramenta interna) — é sobre **não confundir "rascunho de alguém" com
"resposta certa do sistema"**. Um candidato `StatusFailed` ou em iteração intermediária
aparecendo na tela de outro usuário sem contexto ("por que isso não converge, é meu documento
mesmo?") é pior experiência do que não aparecer. O modelo correto pro "compartilhamento" que o
dono imagina é o que **já existe desenhado**: quando o candidato converge e é promovido (§6.1 do
doc-mãe, ação de `admin`), **aí sim** ele vira artefato institucional visível a todos — não
precisa "vazar" o rascunho pra atingir o objetivo de reuso de conhecimento; o objetivo é atingido
pela promoção, que é exatamente o mecanismo que o doc-mãe já apontou como gap a construir.

**Isto refina, não substitui, a issue #92** (particionamento do `AiCandidateStore`): o Passo 1
do §7.2 do doc-mãe continua correto e não muda de escopo — isolar por `ICurrentUser.Name` é a
implementação técnica tanto do argumento (i) quanto de qualquer leitura "consultiva" futura entre
usuários (se um dia o produto quiser permitir "ver o rascunho de fulano", isso é uma **exceção
de leitura explícita sobre uma partição isolada**, não a ausência de partição). Partição por
usuário é pré-requisito nos dois casos — a diferença entre (i) e (ii) é só se o acesso cross-user
é default-deny ou default-allow, e a recomendação aqui é default-deny, com a promoção (§6.1)
sendo o caminho oficial de "isso agora é de todo mundo".

**Não recomendo** desenhar hoje um mecanismo de "compartilhar rascunho entre usuários
específicos" (ex.: usuário A permite que B veja o ticket dele) — não há pedido concreto para
isso, e adicionaria superfície (ACL por ticket) que o doc-mãe já decidiu não precisar
(`MapperDatabaseController` hoje é tudo-ou-nada: aberto ou `admin`, sem meio-termo por usuário).
Se aparecer necessidade real, é extensão pontual sobre a partição já isolada, não retrabalho.

---

## 2. Modelo de persistência — extensão do que existe, não tabela nova (com uma exceção)

### 2.1 O que já existe e o que falta

O doc-mãe (§7.2, Passo 1 e Passo 2) já separa isso corretamente — esta seção só amarra a
pergunta específica do dono ("é extensão ou é novo?") a uma resposta concreta:

| Camada | É extensão do que existe? | Onde |
|---|---|---|
| **Isolamento por usuário no `AiCandidateStore`** (rascunho em andamento) | **Sim — extensão direta.** Trocar chave `ticket` por `{userId}:{ticket}`, mesmo mecanismo (`ConcurrentDictionary` + arquivo), só com dimensão nova. Já é o Passo 1 do doc-mãe. | `AiCandidateStore` (memória+arquivo, TTL curto — natureza de cache continua correta pra isso, é trabalho efêmero) |
| **Histórico consultável de longo prazo** (documentos que o usuário já processou, decisões tomadas) | **Não — é conceito novo.** TTL curto e armazenamento em arquivo (adequado a cache/rascunho) não serve a "histórico" que deveria sobreviver a reinício de processo, expurgo de TTL, e ser consultável/auditável ao longo de meses. | **Tabela SQL nova** — ver §2.2 |
| **Prompt customizado por usuário** | **Extensão pequena** — é um campo associado à partição do usuário, não precisa de agregado próprio isoladamente. Mas se histórico (linha acima) já exige SQL, o prompt customizado deveria morar na mesma tabela/linha de sessão, não em um terceiro lugar. | Junto com a tabela de histórico (§2.2) |
| **Artefato promovido (TCL/XSL/XSLT oficial)** | **Já é SQL hoje** (catálogo de mapeadores, `MapperDatabaseController`/`LayoutDatabaseService`) — só falta o campo de proveniência ("promovido de candidato IA", já apontado como gap em §6.1 do doc-mãe). Não é escopo desta seção, é reafirmação de que a arquitetura já está certa aqui. | Tabela de mapeadores existente + coluna nova |

### 2.2 Por que tabela SQL nova para o histórico, e não estender `AiCandidateStore`

Princípio inegociável do projeto: **SQL é fonte da verdade; Redis/arquivo é cache** — o mesmo
raciocínio se aplica ao `AiCandidateStore` (armazenamento em arquivo com TTL), que foi desenhado
deliberadamente como *cache de trabalho em progresso*, não como registro permanente (é por isso
que tem TTL e é limpo, issue #51). Um "histórico" que o dono quer consultar depois — no espírito
de "sessão que persiste", análogo a poder voltar e ver o que já fiz — **é dado de sistema, não
cache**. Reaproveitar o `AiCandidateStore` para isso violaria a própria razão de ele existir como
está (efêmero, sem garantia de durabilidade, perdido em reinício se não persistido em disco de
forma robusta) e obrigaria a promover TTL curto pra "indefinido" — nesse ponto já não é mais
cache, é a tabela disfarçada de arquivo.

**Proposta de tabela** (nome ilustrativo, desenho não implementação):

```
AiUserSession
  UserId (FK conceitual em ICurrentUser.Name, não há tabela de usuário hoje — string, como já é
          o padrão do projeto para identidade vinda do BFF)
  CustomPromptInstruction (nullable, texto — o "adiciona, não substitui" do §8.1 do doc-mãe)
  CreatedAt / UpdatedAt

AiUserSessionHistoryEntry
  SessionId (FK -> AiUserSession)
  Ticket (mesmo identificador já usado no AiCandidateStore)
  Status (Converged/Failed/Pending — espelha AiCandidateStatus)
  CreatedAt
  -- não duplica o conteúdo do candidato (XSLT gerado, diffs) — isso continua no
  -- AiCandidateStore/arquivo enquanto está "quente"; se convertido para artefato promovido,
  -- o conteúdo definitivo passa a viver no catálogo de mapeadores (fonte única), não aqui.
  -- Esta tabela é índice/histórico de "o que aconteceu", não repositório de conteúdo.
```

Isso evita duplicar conteúdo pesado (XSLT/TCL gerado) em dois lugares — a tabela de histórico
guarda **referência e status**, o conteúdo "quente" continua no `AiCandidateStore` (cache, TTL
curto de verdade) e o conteúdo "definitivo" vive no catálogo de mapeadores quando promovido.
Resolve a tensão entre "SQL é fonte da verdade" e "não duplicar dado grande em dois stores".

---

## 3. A analogia com Claude Code/Codex/ChatGPT — até onde ela se aplica

Honestamente: **a analogia se aplica à ideia de "identidade persistente com histórico e
preferências", não à mecânica de sessão conversacional multi-turno**. São produtos diferentes:

- Claude Code/ChatGPT: uma sessão é uma **conversa** — contexto cresce turno a turno, o modelo vê
  tudo que foi dito antes na mesma janela, e o usuário pode redirecionar em linguagem natural a
  qualquer momento dentro da mesma sessão.
- O pathway de IA aqui é **single-shot por ticket**: gera candidato, valida contra
  `CanonicalDiffer`/`XsdValidationService`, converge ou falha — sem "conversa" com o Ollama sobre
  aquele candidato depois de gerado (confirmado no doc-mãe §8.3: "cada `GenerateCandidateAsync` é
  uma chamada nova", sem memória entre chamadas HTTP).

O que é **realista** trazer da analogia, sem forçar o paralelo além do que o domínio suporta:

1. **Histórico consultável** — "o que eu já processei, o que convergiu, o que falhou" (é a tabela
   §2.2, análogo a histórico de conversas do Claude Code, não à conversa em si).
2. **Prompt customizado persistente** — já desenhado no doc-mãe §8, análogo a instruções
   persistentes de projeto (`CLAUDE.md`/preferências de sessão), não a uma mensagem dentro de um
   chat.
3. **Retomar/iterar sobre uma transformação anterior** — isso é o mais próximo de "sessão" no
   sentido multi-turno, e **já existe parcialmente**: o pathway de IA já itera internamente
   (tentativa anterior + diff, citado no doc-mãe §8.1) até convergir ou esgotar tentativas. O que
   não existe é o usuário **retomar manualmente** um ticket já finalizado (`StatusFailed`) com uma
   instrução nova ("tenta de novo, mas considera X") depois que o loop automático desistiu. Isso é
   uma extensão pequena e realista: um endpoint que recebe `ticket` + `userInstruction` adicional
   e reenfileira, sem precisar reconstruir o conceito de "sessão" do zero — a tabela de histórico
   (§2.2) já dá o rastro de qual ticket pertence a qual usuário para permitir essa retomada com
   segurança (não deixar usuário B retomar o ticket de A).

**Não recomendo** desenhar um chat multi-turno de verdade (usuário conversando livremente com o
LLM sobre a transformação) — não há pedido para isso, o domínio (transformação de documento
fiscal validada por diff determinístico) não se beneficia de conversa livre, e adicionaria
superfície de prompt injection (§8.3 do doc-mãe) sem ganho correspondente.

**Isto refina a issue #97** (sessão por usuário): o escopo realista de "sessão" para esta
ferramenta é histórico + prompt persistente + retomada pontual de ticket — não infraestrutura de
chat. Se a #97 foi redigida com linguagem de "sessão conversacional", vale ajustar a descrição
para não sugerir escopo maior do que o desenhado aqui.

---

## 4. Geração de mapeamento a partir de layout + gabarito SEFAZ + lógica fiscal

### 4.1 A pergunta central: extensão do RAG atual, ou capacidade nova?

O pathway hoje (`AiTransformationCandidateService.BuildPrompt`, doc-mãe §8.1) é **RAG few-shot
por padrão de exemplo**: monta prompt com layout + mapeador + entrada + gabarito, pede ao Ollama
para gerar XSLT que bata no diff. Funciona bem quando a transformação-alvo é **estruturalmente
parecida** com exemplos já vistos — é reconhecimento de padrão, não derivação de regra.

O que o dono está pedindo — "montar o mapeamento a partir da **lógica de implementação
fiscal**" — é qualitativamente diferente: não é "encontre um exemplo parecido e adapte", é
"entenda **por que** o campo X do gabarito SEFAZ tem aquele valor dado aquele layout de entrada,
mesmo sem um exemplo prévio idêntico". Isso é mais próximo de **raciocínio sobre regra de
negócio** do que de correspondência de padrão.

### 4.2 Reconectando com o achado já registrado sobre a fronteira do XSLT

O doc-mãe (§4, reaproveitando `viabilidade-dlls-sysmiddle-para-rag.md` §5) já mapeou que XSLT
cobre bem **condicional, formatação, cálculo simples, lookup em tabela estática**, mas é
**fraco** para **estado mutável complexo entre elementos não-relacionados na árvore** — e lógica
fiscal real (ex.: determinar CFOP correto, cálculo de tributo que depende de múltiplas seções do
documento e de regras externas ao próprio XML) frequentemente cai exatamente nessa categoria
fraca. Isso importa aqui de duas formas:

1. **A saída final** (o XSLT gerado) continua sujeita à mesma fronteira de expressividade — não
   importa quão bem o LLM "entenda" a regra fiscal, se a regra não é expressável em XSLT puro
   (precisa de estado cross-seção, chamada externa), o XSLT gerado não vai conseguir implementá-la
   corretamente. Isso é limite de **ferramenta de saída**, não de raciocínio de entrada.
2. **O raciocínio de entrada** (entender a regra) é uma capacidade diferente da capacidade de
   **gerar sintaxe XSLT correta** — hoje o loop RAG→Ollama→XSLT pede as duas coisas de uma vez
   numa única geração, o que é pedir demais de um modelo pequeno (CPU-only, 1-2B, conforme
   `production-server-hardware.md`) sem processo de derivação de regra estruturado.

### 4.3 Veredito: capacidade nova, recomendo two-step

**Não é extensão simples do RAG atual.** Recomendo separar em duas etapas distintas:

1. **Etapa 1 — derivar a regra em linguagem estruturada.** Dado o layout de entrada + o gabarito
   SEFAZ (par completo, não truncado como hoje em `Truncate(inputContent, 4000)` — para essa
   etapa a granularidade da regra importa mais que economia de tokens), o LLM produz uma
   **descrição estruturada da transformação campo-a-campo** (ex.: "campo `CFOP` do gabarito =
   lookup em tabela X pelo par (`tipoOperacao`, `UF`) do layout de entrada, com fallback Y quando
   Z") — não XSLT ainda, uma representação intermediária mais próxima de regra de negócio
   verbalizada/tabular.
2. **Etapa 2 — gerar o XSLT a partir da regra estruturada**, não a partir do exemplo cru. Essa
   etapa é mais próxima do que o pathway já faz bem hoje (é geração de sintaxe a partir de uma
   especificação, não inferência de padrão) — e ainda passa pelo mesmo verificador determinístico
   (`CanonicalDiffer` + `XsdValidationService`) que já existe.

**Trade-offs do two-step:**

| | Prós | Contras |
|---|---|---|
| **Two-step (recomendado)** | Separa "entender a regra" de "escrever XSLT correto" — cada etapa é mais tratável para um modelo pequeno; a regra estruturada intermediária é **auditável por humano** (um analista fiscal pode revisar/corrigir a regra derivada antes que vire XSLT — ponto de controle que hoje não existe); reaproveita o verificador existente sem mudança | Mais caro (duas chamadas Ollama em vez de uma, CPU-only já é o gargalo conhecido); precisa desenhar o formato da "regra estruturada" (schema novo) e um novo prompt/parser para essa etapa; se a Etapa 1 errar a derivação da regra, a Etapa 2 gera XSLT sintaticamente correto mas semanticamente errado — o diff final ainda pega isso, mas o erro fica mais difícil de depurar (em qual etapa surgiu) |
| **Direto (atual, exemplo→XSLT)** | Já existe, sem trabalho novo de design; mais barato (uma chamada) | Já demonstrou (por natureza do mecanismo RAG) que só generaliza bem perto de exemplos vistos — não é o que o dono está pedindo, que é generalizar **sem** exemplo prévio idêntico via entendimento da regra |

**Não recomendo** tentar resolver isso só ajustando o prompt do pathway atual (ex.: "explique seu
raciocínio antes de gerar o XSLT" na mesma chamada) — isso não separa de fato as duas
capacidades, só pede ao modelo pequeno para fazer as duas coisas em sequência dentro do mesmo
contexto/chamada, sem o ponto de controle intermediário (revisão humana da regra) que é o
principal ganho do two-step real. Se o objetivo é robustez (o "objetivo de longo prazo" citado
pelo dono), vale o custo de duas chamadas.

**Isto é uma extensão de escopo, não um ajuste — não corresponde a nenhuma issue já aberta**
(#92/#93/#97/#98 são sobre RBAC/sessão/particionamento, não sobre o mecanismo de geração em si).
Recomendo que `@lp-pm` trate como item de backlog **novo**, sequenciado depois da estabilização
do pathway atual (não compete com #92/#93 por prioridade imediata) — é pesquisa/design maior, não
uma correção.

---

## 5. Entregáveis para os próximos agentes

- `@lp-pm`: **atualizar #92** com a confirmação de que particionamento por usuário é o desenho
  correto mesmo em modelo "ferramenta interna, tudo é da NDD" (default-deny entre usuários,
  promoção como caminho de compartilhamento — §1.2 acima); **refinar #97** para escopo realista
  de sessão (histórico + prompt persistente + retomada pontual de ticket, não chat multi-turno —
  §3 acima); **abrir issue nova** para o item novo do §2.2 (tabela `AiUserSession`/
  `AiUserSessionHistoryEntry`, distinta do `AiCandidateStore`) se ainda não coberta por #97/#98;
  **abrir issue nova e separada** (não misturar com #92-#98) para a geração de mapeamento
  layout+gabarito+regra fiscal (two-step, §4.3) — é pesquisa/design maior, sequenciar depois da
  estabilização do RBAC/sessão.
- `@lp-backend-dev`/`@lp-parser-llm`: nenhuma mudança de código aqui — este documento é insumo
  para as issues acima; a implementação de #92 (Passo 1 do doc-mãe) segue sendo o pré-requisito
  imediato antes de qualquer trabalho de histórico/sessão de longo prazo.
- **Dono**: confirmar a recomendação do §1.2 (isolamento por padrão, compartilhamento via
  promoção) antes de o `@lp-pm` fechar a redação da #92; e sinalizar se a extensão do §4
  (geração de mapeamento a partir de regra fiscal) é prioridade de curto prazo ou de fato "visão
  de longo prazo" (como descrito na missão) — isso muda o sequenciamento no backlog.

---

*LayoutParser API · Sessão de usuário e artefatos compartilhados · `@lp-architect` · 2026-08-14*
