# ADR — Geração automática de TCL/XSL/XSLT com gabarito Sysmiddle

- **Data:** 2026-09-17
- **Status:** Proposto
- **Autor:** `@lp-architect` (Aria)
- **Issue de referência:** `LayoutParser/LayoutParserApi#438`
- **Fundação:** #434 (fix MQSeries), #435 (loop RAG+Ollama), #437 (catálogo de exemplos
  Neogrid), #433/#439 (fix layout-tree `target.roots`)

## 1. Contexto

O dono definiu dois requisitos novos para o pipeline de síntese de TCL/XSL/XSLT:

1. **Geração automática.** Hoje a síntese só acontece quando um agente/dev dispara
   manualmente `ai/XslSynth` (CLI). Quando um mapeador Sysmiddle existe no catálogo
   (`tbMapper`), o time de operações precisa ver o TCL/XSL/XSLT gerado **sem intervenção
   manual** — "pra visualização do time de operações já".
2. **Gabarito de validação = Sysmiddle real, não os exemplos Neogrid.** O corpus exposto em
   `#437` (`GET /api/reference-examples`, `Services/Fiscal/ReferenceExampleCatalogService`)
   continua servindo como referência de estilo/RAG, mas **não é o critério de correção**. Se o
   gerado divergir do comportamento real do Sysmiddle, o erro é nosso, nunca do Sysmiddle. Não
   estamos recriando mapeadores que já funcionam — estamos produzindo e validando o
   equivalente TCL/XSL, com o Sysmiddle como fonte da verdade de **comportamento**.

## 2. Estado real do bloqueio de execução do Sysmiddle (confirmado nesta sessão)

Li `ai/XslSynth/Program.cs` (fluxo `RunAsync`, em torno da linha 1205) e a memória de
`poc3-r4-estado` (2026-07-12, `@lp-parser-llm`). Confirmo que o bloqueio **ainda se aplica**:

> `Log("── Limite honesto (o que falta p/ fechar o loop diff==0) ─────────");`
> `Log("   • sem gabarito de runtime ainda: validamos COMPILAÇÃO + COBERTURA, não igualdade.");`
> `Log("   • o gabarito virá do host FiatMQ (ver docs/architecture/ia-xslt-synthesis.md §9).");`

O runner Sysmiddle in-process trava na inicialização de licença do host FiatMQ. Isso não mudou
desde julho. Hoje **não é possível** rodar o mapeador real e obter uma saída de execução para
diff campo a campo — o `CoverageValidator` atual valida só compilação (`XslCompiledTransform`)
e cobertura de `LinkMapping`/`Rule` referenciados, não igualdade de saída.

### O que já existe como aproximação: o DSL decifrado como gabarito semântico

`RealMapperParser` (`ai/XslSynth.Contracts/Core/RealMapperParser.cs`) já decodifica o mapeador
Sysmiddle real em `MapperVo` — `LinkMappingItem` (237 links, no caso do mapper FiatMQ) +
`MapperRule` (98 regras) — e a `poc3-r4-estado` confirma que esse DSL **já é usado como "máscara
de emissão"**: a Etapa B daquela sessão usava explicitamente os 237 links + 98 regras do
MapperVO real para decidir o que o gerador determinístico deveria omitir/emitir (ex.: por que o
mapeador omite `retTrib` zerado mas emite `fat/vOrig,vDesc` zerados — decisão que só o mapeador
real carrega, e que nenhum spec-Excel expressa).

**Isso é uma aproximação real, não um substituto do runtime — e a diferença importa:**

| | DSL decifrado (`MapperRule`/`LinkMappingItem`) | Execução real do Sysmiddle |
|---|---|---|
| O que garante | Que o TCL/XSL gerado **implementa a mesma regra declarada** (mesmo path de origem→destino, mesma condição de emissão declarada) | Que o TCL/XSL gerado **produz o mesmo byte-a-byte** com um documento real de entrada |
| O que NÃO garante | Efeitos de runtime do interpretador Sysmiddle não expressos na regra declarada (ordem de avaliação, coerção de tipo implícita, comportamento de erro/fallback do motor) | — (é o gabarito verdadeiro) |
| Já temos hoje? | Sim (`RealMapperParser`, `MapperVo`) | Não (bloqueio de licença FiatMQ) |

**Recomendação:** usar o DSL decifrado como gabarito **é aceitável como aproximação temporária**,
desde que isso seja declarado explicitamente no relatório de validação (não maquiado como
"validado contra o Sysmiddle"). Ele já é estritamente melhor que os exemplos Neogrid como
critério de correção, porque é a **definição declarada** do comportamento daquele mapeador
específico — os exemplos Neogrid são estilo de outro corpus, não a regra deste mapper. O
`CoverageValidator` de hoje (compila + cobre 100% dos links/rules do `MapperVo`) já é,
estruturalmente, uma comparação candidato-vs-DSL — falta só nomear isso corretamente no
relatório e tratar "cobertura 100%" como sinal necessário, não suficiente, de correção
semântica.

Não recomendo esperar o desbloqueio da licença FiatMQ para começar a entregar valor — não há
horizonte conhecido para esse desbloqueio, e o requisito 1 (visualização automática) não depende
dele.

## 3. Trigger automático — desenho

Avaliei três opções:

**(a) Evento observável em `tbMapper`.** Não existe hoje um mecanismo de evento/webhook no
catálogo `tbMapper` (é uma tabela SQL simples, lida via `MapperDatabaseService`/
`CachedMapperService`, sem CDC nem trigger de aplicação). Implementar isso exigiria polling
periódico de qualquer forma (comparar `tbMapper` contra o que já foi gerado) — ou instrumentar
DDL/CDC no SQL, que esbarra na regra "somente leitura fora de `IdentityDatabase`" para o banco
compartilhado onde `tbMapper` vive. Descartado como mecanismo primário.

**(b) Trigger lazy no `GET .../layout-tree` / endpoint de release.** Quando alguém (o painel de
operações, via `GET /api/fiscal/layout-tree` ou um futuro `GET .../generated-transformation`)
pede o artefato de um mapper que ainda não tem candidato gerado, dispara a síntese em
background (fire-and-forget, mesmo padrão de `LowCodeAutoTransformationService
.RunInBackgroundAsync` já usado no projeto) e devolve **o que houver disponível no momento**
(nada na primeira consulta, ou o candidato pronto em consultas seguintes).

**(c) Job periódico dedicado (worker/cron) que varre `tbMapper` e gera para mappers sem
candidato.** Cobre o caso "ninguém olhou ainda, mas eu quero que já esteja pronto quando
olharem" — que a opção (b) sozinha não cobre (primeira consulta sempre vem vazia).

**Decisão: combinar (b) + (c).**

- (b) cobre resiliência de resposta ao usuário sem sobrecarga: só gera o que é efetivamente
  pedido, nunca todo o catálogo de uma vez. Casa com o princípio de resiliência do projeto —
  não bloqueia o request principal (retorna imediatamente com o que existir, dispara geração à
  parte) e não derruba a API se Ollama estiver fora do ar (mesmo padrão de captura/log/degradação
  já usado em `RunInBackgroundAsync`).
- (c) fecha a lacuna de "time de operações abre o painel e já quer ver pronto, sem ser o primeiro
  a pedir". Um job (`IHostedService`/`BackgroundService`, análogo ao job de métricas que já roda
  na VM Ubuntu aos sábados) varre `tbMapper` periodicamente (ex.: a cada N horas, configurável),
  filtra mappers sem candidato publicado ainda, e enfileira geração com **limite de concorrência
  baixo** (1–2 por vez) para não saturar o Ollama local (CPU-only, `BRNDDAPPBLD01` — já mapeado
  como hardware fraco em memória de `@lp-architect`). Prioriza mappers recém-adicionados
  primeiro.
- **Sem (a):** nenhuma das duas opções depende de instrumentar o SQL compartilhado — ambas usam
  apenas leitura de `tbMapper` (já permitida) e escrita no armazenamento próprio do projeto
  (`IdentityDatabase`/artefato de candidato), respeitando a regra de somente-leitura em
  `172.31.249.51`.

## 4. Loop de validação contínua

"Sempre validar contra o Sysmiddle" não é um evento único — o mapeador Sysmiddle pode ser
atualizado (nova versão de `tbMapper`), e o candidato gerado precisa acompanhar.

**Gatilho de revalidação:** o mesmo job periódico da opção (c) acima, ao varrer `tbMapper`,
compara um hash/versão do `MapperVo` decodificado (não do XML criptografado bruto — o hash deve
ser sobre `LinkMappingItem`/`MapperRule` já normalizados, para não gerar falso-positivo por
diferença de formatação irrelevante) contra o hash registrado no sidecar de proveniência
(`generated-provenance.json`, já existente via `ProvenancePublisher`) do candidato publicado.
Se divergir, o mapper entra na fila de regeração como se fosse novo. Isso reaproveita a
infraestrutura de proveniência que já existe (A6, `ai/XslSynth/Core/ProvenancePublisher.cs`) sem
inventar um mecanismo de versionamento paralelo.

**Não propor** invalidação por polling constante (a cada minuto) — a frequência do job (c) já
citada é suficiente, dado que atualização de mapeador Sysmiddle é evento raro (dias/semanas), não
contínuo.

## 5. Escopo mínimo viável para a próxima implementação

O menor passo que entrega valor real sem prometer o que o bloqueio de licença impede:

1. **Endpoint de leitura** (`GET /api/fiscal/mappers/{mapperGuid}/generated-transformation` ou
   equivalente) que devolve o candidato publicado mais recente (`candidate.published.xslt` +
   `generated-provenance.json`) se existir, com um campo explícito de status
   (`"none" | "generating" | "ready" | "stale"`).
2. **Trigger lazy (opção b)** nesse endpoint: se `"none"`, dispara `RunInBackgroundAsync` do
   fluxo de síntese (adaptar `ai/XslSynth` para rodar como serviço in-process da API, não só
   CLI — hoje é um projeto console separado; isso é o item de maior esforço real deste MVP) e
   responde imediatamente com `"generating"`.
3. **Relatório de validação honesto:** o campo de cobertura já existente (`CoverageValidator`)
   passa a ser rotulado explicitamente como "cobertura contra a definição declarada do mapeador
   (`MapperVo`/DSL decifrado)" — não "validado contra o Sysmiddle real" — para não criar falsa
   confiança no time de operações. Isso é puramente rótulo/contrato de resposta, sem novo
   código de validação.
4. **Fora deste MVP** (depende do desbloqueio de licença FiatMQ, sem data): diff campo a campo
   contra execução real, job periódico (c) de geração proativa, e hash de versão do mapper para
   revalidação automática (seção 4) — documentados aqui como próximos passos, não implementados
   agora.

Esse escopo já resolve o requisito 1 (operações vê o gerado sem pedir a um dev para rodar o
CLI manualmente) e torna o requisito 2 tecnicamente honesto (gabarito é o DSL decifrado, rotulado
como tal) sem fingir que o Sysmiddle real foi executado.

## 6. Riscos e trade-offs

- **Risco de confiança:** se o rótulo "cobertura vs. DSL" não for suficientemente visível no
  painel de operações, alguém pode tratar "100% de cobertura" como "validado contra produção".
  Mitigação é de UX/contrato de API, não arquitetura — sinalizar a `@lp-doc`/frontend.
- **Custo de Ollama:** geração lazy sob demanda evita pico, mas se vários mappers novos entrarem
  de uma vez (ex.: import em lote), o job periódico (c) pode competir por CPU com requisições
  lazy simultâneas. Limitar concorrência total (lazy + job) a um semáforo único no processo,
  não dois limites independentes.
- **Sem event source real em `tbMapper`:** a dependência de polling periódico (c) significa que
  a "automação" tem uma janela de atraso (não é instantânea). Aceitável dado que atualização de
  mapeador é evento raro; documentar essa latência no README/contrato se o dono perguntar.

## Estado da implementação (2026-09)

Acrescentado após a implementação da issue #438. O corpo da decisão acima não foi alterado; esta
seção só registra onde a implementação real se desviou ou precisou de detalhe adicional.

- **Persistência em tabela própria, não em `IMappingReleaseStore`.** O artefato gerado ficou na
  tabela nova `tbGeneratedMapperArtifact` (criada em
  [`Services/Database/FiscalSchemaInitializer.cs`](../../Services/Database/FiscalSchemaInitializer.cs),
  acesso via [`SqlGeneratedMapperArtifactStore`](../../Services/Database/SqlGeneratedMapperArtifactStore.cs) /
  [`IGeneratedMapperArtifactStore`](../../Services/Interfaces/IGeneratedMapperArtifactStore.cs)),
  autossuficiente e sem FK para as tabelas de release. Vive no `IdentityDatabase:*`, nunca no SQL
  compartilhado `172.31.249.51`.
- **Unificação com `mapping-releases` decidida em outro ADR.** A relação entre o artefato gerado e o
  ciclo de vida de `MappingRelease` (listagem unificada, sem fundir as tabelas) está em
  [`adr-unificacao-generated-artifact-mapping-release.md`](adr-unificacao-generated-artifact-mapping-release.md).
- **Gabarito = DSL declarado, rótulo explícito.** Como o runner Sysmiddle segue bloqueado por
  licença (FiatMQ), o gabarito é o DSL decifrado e o contrato o rotula com
  `validationBasis: "declared_dsl"` (constante `ValidationBasisDeclaredDsl` em
  [`GeneratedMapperArtifactService.cs`](../../Services/Transformation/Ai/GeneratedMapperArtifactService.cs)),
  em linha com o item 3 do escopo da seção 5. Diff contra execução real, job periódico e hash de
  versão continuam fora do escopo implementado.
- **Ponto de entrada:** [`GeneratedMapperArtifactController`](../../Controllers/GeneratedMapperArtifactController.cs).
