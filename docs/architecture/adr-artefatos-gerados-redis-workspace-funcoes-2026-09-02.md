# ADR — Redis como fonte da verdade para TCL/XSLT, workspace por usuário e classificação de funções (2026-09-02)

> **PT-BR.** Autoria: `@lp-architect`. Decisões confirmadas pelo dono durante um exercício prático
> de finalização de `Rule_gIBSCBSMono` (regra de mapeamento IBS/CBS monofásico, Reforma Tributária,
> NT2025.002). Não implementa nada — formaliza 4 decisões de arquitetura pra orientar o trabalho
> futuro de `@lp-parser-llm` (Lia) na geração automática de TCL/XSLT via RAG/Ollama (sem
> fine-tuning — ver `no-fine-tuning-ai-decision`, memória de `@lp-architect`).

## 1. Contexto

Nesta sessão, o dono e o coordenador conduziram um exercício de referência: finalizar a regra
`Rule_gIBSCBSMono` seguiu o fluxo (1) detectar mudança de nomes de campo no layout de input,
(2) confirmar a estrutura de destino correta contra o XSD oficial (`TMonofasia` em
`DFeTiposBasicos_v1.00.xsd`, NT2025.002, `.claude/temp/treino/PL_010f_v1.04/`), (3) só então gerar
a regra final na DSL do Sysmiddle (`if/begin/end`, `I.`/`T.`, `FormaterDecimal(...)`). Esse
exercício é declaradamente um **caso de treino/referência**: o que o Ollama via RAG precisa
aprender a fazer sozinho é gerar TCL e XSL/XSLT a partir de layout(s) + schema, seguindo o mesmo
processo (detectar estrutura → validar contra schema → gerar).

Ao longo da sessão, inspecionando o Redis ao vivo (`localhost:6379`, chaves `layouts:search:all` e
chaves de mapper) e discutindo o papel do TCL, o dono tomou 4 decisões que fecham questões antes
em aberto no roadmap de IA. Este documento registra as 4, no mesmo espírito de
`visao-migracao-sysmiddle-para-tcl-xslt-2026-08-30.md` (que já havia estabelecido TCL/XSLT como
alvo de migração) e `viabilidade-dlls-sysmiddle-para-rag.md` (que já havia mapeado Layout/Mapper
como fonte de RAG) — este ADR é o próximo elo: como os artefatos *gerados* pela IA são
armazenados, versionados por usuário, e o que a IA deve aprender a fazer com as funções
customizadas encontradas no Mapper.

## 2. Decisão 1 — Redis é a fonte da verdade para TCL/XSLT gerados, não só para o catálogo Sysmiddle

**Decisão.** O Redis já é a fonte da verdade do catálogo de layouts/mappers do Sysmiddle
(confirmado ao vivo nesta sessão). O mesmo princípio se estende aos artefatos que o pipeline
RAG/Ollama gerar: **TCL e XSL/XSLT de produção vivem no Redis**, não em pasta solta no
filesystem. As pastas de exemplo no filesystem (os 259 pares `.tcl`/`.xsl` já mapeados em
`visao-migracao-sysmiddle-para-tcl-xslt-2026-08-30.md`) continuam existindo — mas como **material
de treino/referência (padrão-ouro)**, não como o repositório de produção.

Chave natural proposta (refinável por `@lp-devops`/`@lp-parser-llm` na implementação, mas a
lógica de amarração já foi validada com o dono):

```
mapper:{MapperGuid}:tcl                        -> versão "produção" (TCL, parser interno da ferramenta)
mapper:{MapperGuid}:xslt                       -> versão "produção" (XSLT real, o que roda a transformação)
mapper:{MapperGuid}:workspace:{userId}:tcl     -> cópia de trabalho (draft) do usuário
mapper:{MapperGuid}:workspace:{userId}:xslt    -> cópia de trabalho correspondente
```

**Por que amarrar TCL e XSLT pela mesma chave (`MapperGuid`).** Elimina ambiguidade de "qual XSLT
corresponde a qual TCL" — não precisa de heurística de busca de candidatos, é 1:1 garantido
estruturalmente. Quando o usuário seleciona um TCL (layout TXT) pra visualizar/parsear, o sistema
já sabe exatamente qual XSLT ele representa.

**Consequências e trade-offs.**

| Ganho | Custo/risco |
|---|---|
| Elimina divergência entre "o que está em produção" e "o que está no disco de alguém" | Redis vira dependência dura para a *escrita* de artefatos gerados — hoje o padrão do projeto trata Redis como cache opcional (degrada sem ele); artefato de produção sem persistência de disco muda esse cálculo, ver observação abaixo |
| Chave determinística por `MapperGuid` simplifica lookup e evita duplicação de lógica de "achar o par certo" | Precisa de política de TTL/persistência — Redis como *fonte da verdade* (não cache) exige `RDB`/`AOF` habilitado e backup, diferente do uso atual como cache best-effort |
| Reaproveita infraestrutura já operacional (Redis já sobe no ambiente) | Nenhuma migração de schema SQL necessária agora, mas se o time decidir mais tarde que TCL/XSLT de produção precisam de auditoria/histórico forte, Redis sozinho não tem controle transacional — reavaliar então |

**Observação de resiliência (obrigatória pelo padrão do projeto).** Como este artefato passa a
ser "fonte da verdade" e não cache, a config de Redis para este uso específico não pode seguir o
padrão de "app sobe sem Redis, degrada" sem mais — é preciso decidir explicitamente o que acontece
se o Redis cair no meio de uma geração/consulta de TCL/XSLT em produção (ex.: fallback de leitura
para uma cópia espelhada em SQL/disco, ou aceitar indisponibilidade daquele fluxo específico até o
Redis voltar). Essa decisão de resiliência concreta (RDB/AOF, backup, fallback de leitura) é
**responsabilidade de `@lp-devops`** antes de qualquer chave de produção ser escrita — este ADR
não a resolve, só sinaliza que ela existe e precisa ser fechada antes do rollout.

**Dono.** `@lp-parser-llm` (implementação do pipeline de leitura/escrita das chaves) +
`@lp-devops` (schema de persistência/TTL/backup do Redis para este uso).

## 3. Decisão 2 — TCL é parser interno (visualização); XSL/XSLT é o formato real de transformação

**Decisão** (textual do dono): *"TCL ele é para 'visualização', tipo parser que a nossa
ferramenta já tem, é muito mais simples fazer um parser com o TCL... já devemos estar
provisionando inclusive esse parseamento futuro, já que a migração ela vai acontecer alguma
hora."*

Leitura arquitetural: o TCL **não é o alvo final** da geração por IA — é uma representação
intermediária, mais simples de parsear/exibir na ferramenta, do layout posicional (TXT). O XSLT é
o artefato que efetivamente processa a transformação em produção, aplicado sobre o XML "refinado"
do Sysmiddle (não XSLT bruto arbitrário). Isso confirma e reforça, sem contradizer, o pathway já
descrito em `visao-migracao-sysmiddle-para-tcl-xslt-2026-08-30.md` (§1: "TXT → TCL → XSL/XSLT →
XML"): o TCL resolve a ponta de *layout posicional*, o XSLT resolve a ponta de *transformação*.

**Consequência prática — provisionar o parser nativo de TCL desde já.** O dono é explícito de que
a "migração" (descontinuar a dependência do formato/engine do Sysmiddle) vai acontecer em algum
momento — não é hipotético. A arquitetura deve **já prever** um parser de TCL nativo na ferramenta
(fora do runtime Sysmiddle), mesmo antes de o pathway de migração estar 100% pronto, para não
represar essa peça como último passo de uma migração maior.

| Trade-off | Detalhe |
|---|---|
| Construir o parser TCL cedo, mesmo com poucos casos gerados por IA ainda | Investimento adiantado; risco de retrabalho se o formato TCL de saída da IA divergir do formato TCL humano já em produção (259 pares) — mitigação: usar os pares humanos como especificação de formato, não inventar dialeto novo |
| Adiar o parser até ter volume maior de TCLs gerados | Simplicidade agora, mas empurra o boundary Windows/Sysmiddle pra frente — contraria a intenção explícita do dono de já provisionar a migração |

Recomendação: adiantar, seguindo o padrão-ouro dos 259 pares humanos como especificação de
formato — decisão já alinhada com `visao-migracao-sysmiddle-para-tcl-xslt-2026-08-30.md`, que
trata esses pares como gabarito de aceitação, não dataset descartável.

**Dono.** `@lp-parser-llm` (desenho e implementação do parser TCL nativo, quando priorizado).

## 4. Decisão 3 — Workspace de cópia de trabalho por usuário reaproveita o Slice 1 de identidade

**Decisão.** Quando um usuário quer trabalhar num mapeador/TCL/XSLT já "em produção", o sistema
cria uma cópia de trabalho isolada por usuário (branch de rascunho), com apoio do RAG/Ollama para
desenvolvimento incremental — copy-on-write, não edição direta do artefato de produção.

Este modelo **não deve reinventar mecanismo de workspace paralelo**: já existe base de dados
pronta desta mesma sessão/branch — `Services/Database/SqlIdentityWorkspaceStore.cs` /
`IdentityWorkspaceService.cs`, SQL Server dedicado, tabelas `tbLpUser` / `tbLpExternalIdentity` /
`tbLpFiscalWorkspace` / `tbLpWorkspaceMembership` (branch `fix/identity-sql-local-db`, já
mergeado em `develop`/`master` via PRs #254-257). O modelo de cópia de trabalho no Redis
(`mapper:{MapperGuid}:workspace:{userId}:...`, ver §2) deve se apoiar no `WorkspaceId` que esse
sistema de identidade já resolve.

Isso é consistente com — e refina — a decisão já registrada em
`sessao-usuario-e-artefatos-compartilhados-2026-08-14.md` (isolamento por padrão + promoção como
caminho de compartilhamento): o `WorkspaceId` do Slice 1 vira o mecanismo concreto de isolamento
que aquele documento previa em abstrato.

**Consequências.**

| Ganho | Custo/risco |
|---|---|
| Zero trabalho de esquema de identidade novo — reaproveita PRs #254-257 já mergeados | Acopla o pipeline de geração de artefatos ao schema de identidade SQL; qualquer mudança futura em `tbLpFiscalWorkspace` afeta também o Redis de artefatos gerados |
| Modelo de promoção (workspace → produção) fica natural: promover = copiar de `workspace:{userId}` para a chave de produção no Redis | Precisa decidir a política de conflito quando dois usuários promovem workspaces divergentes do mesmo `MapperGuid` — não resolvido neste ADR, fica como próxima pergunta para `@lp-parser-llm`/dono quando o fluxo de promoção for desenhado |

**Dono.** `@lp-parser-llm` (integração do fluxo de geração/edição ao `WorkspaceId` existente).

## 5. Decisão 4 — Classificar funções por origem (NDD vs Sysmiddle) e por replicabilidade em XSLT puro

**Achado do dono, direto da inspeção do Redis nesta sessão**: mappers já em produção chamam
`FormaterDecimal` (customizada da NDD) e `DecimalFormatter` (padrão do Sysmiddle) — funcionalmente
equivalentes em alguns casos, mas de origem/DLL diferente.

**Decisão** (textual): *"eu quero que o Ollama não dependa da DLL, eu quero que ele crie a
lógica e, caso não conseguir replicar a lógica daquela função, aí terá que ser via código."*

Leitura arquitetural: o objetivo de longo prazo não é o Ollama "chamar" `FormaterDecimal`/
`DecimalFormatter` como caixa-preta via reflection/DLL (nem da NDD, nem do Sysmiddle) — é **aprender
a replicar a lógica dessas funções diretamente no XSLT gerado** (ex.: uma função de formatação
decimal vira `xsl:function`/lógica XPath inline equivalente), evitando acoplamento a bibliotecas
externas. Só quando a lógica for genuinamente impossível de replicar em XSLT puro é que a geração
cai para fallback via extensão/código externo — caminho de exceção, não o caminho padrão.

Isso **confirma e opera** a fronteira já mapeada em `viabilidade-dlls-sysmiddle-para-rag.md` §5
("Fronteira real de cobertura do XSLT" — tabela de o que XSLT cobre nativamente vs. o que exige
I/O/estado complexo). Este ADR acrescenta o passo concreto que faltava: **catalogar cada função
usada nos mappers reais** (via inspeção do Redis, como feito nesta sessão) em duas categorias:

| Categoria | Ação |
|---|---|
| **Replicável em lógica pura** (ex.: formatação decimal, concatenação, truncamento, condicional) | Vira `xsl:function`/XPath nativo no XSLT gerado — não referencia DLL nenhuma |
| **Requer código externo** (I/O de rede, consulta SQL em tempo de execução, estado mutável complexo — categorias já descritas em `viabilidade-dlls-sysmiddle-para-rag.md` §5) | Fallback explícito via extensão de código na própria API, documentado caso a caso |

Essa catalogação também resolve, na prática, o "gap das Functions customizadas" deixado em aberto
em `viabilidade-dlls-sysmiddle-para-rag.md` §4 (hipóteses (a) chamada dentro do DSL vs (b)
artefato separado) — ao inspecionar o Redis nesta sessão o dono já confirmou que funções como
`FormaterDecimal`/`DecimalFormatter` aparecem como chamadas nomeadas dentro do `ContentValue`
(hipótese (a)), o que simplifica a extração: não é preciso um novo tipo de fonte, só um catálogo de
equivalência função-Sysmiddle/NDD → lógica XSLT nativa.

**Consequências e trade-offs.**

| Ganho | Custo/risco |
|---|---|
| Remove dependência de runtime proprietário do artefato gerado — alinhado ao objetivo maior de "eliminar Sysmiddle" (`visao-migracao-sysmiddle-para-tcl-xslt-2026-08-30.md` §1) | Exige trabalho de catalogação manual/semi-automática antes de escalar o RAG — não é grátis; cada função nova encontrada nos 259 pares precisa ser classificada uma vez |
| Dataset de treino/RAG fica mais rico: em vez de "chame `FormaterDecimal`", o exemplo ensina a lógica equivalente em XSLT — generaliza melhor para funções não vistas | Risco de erro de replicação silencioso (a lógica reimplementada em XSLT diverge sutilmente da função original) — precisa do mesmo framework de diff estruturado XML/XSLT já sinalizado como pendência em `visao-migracao-sysmiddle-para-tcl-xslt-2026-08-30.md` §4, item 3, para pegar essas divergências no loop de validação |
| Fallback explícito para código evita forçar XSLT em casos que genuinamente não cabem (I/O, estado) | Precisa de critério claro de "quando desistir de replicar e cair pro fallback" — recomendação: só cair pro fallback depois de N tentativas de geração falharem a validação, não à primeira dificuldade, para não abandonar cedo demais casos replicáveis |

**Dono.** `@lp-parser-llm` (catalogação das funções, construção do "dicionário" função→XSLT
nativo, extensão do dataset de treino/RAG).

## 6. Resumo — quem faz o quê a seguir

| Decisão | Responsável principal | Depende de |
|---|---|---|
| 1. Redis como fonte da verdade (TCL/XSLT) | `@lp-parser-llm` (pipeline) + `@lp-devops` (persistência/backup/fallback do Redis) | Nenhuma pendência bloqueante — pode iniciar desenho |
| 2. TCL como parser interno, provisionar cedo | `@lp-parser-llm` | Formato-espelho dos 259 pares humanos (`Examples\tcl\`) |
| 3. Workspace por usuário via Slice 1 de identidade | `@lp-parser-llm` | `WorkspaceId`/`tbLpFiscalWorkspace` já mergeados (PRs #254-257) |
| 4. Classificação de funções NDD/Sysmiddle por replicabilidade | `@lp-parser-llm` | Inspeção de mappers reais no Redis (já iniciada nesta sessão) |

Nenhuma implementação de código foi feita neste documento — é registro de decisão de arquitetura.
Pronto para revisão; push e abertura de PR ficam com `@lp-devops` quando o dono confirmar.

---

*LayoutParser · ADR — Artefatos gerados (Redis), workspace por usuário, classificação de funções ·
v1 · `@lp-architect`*
