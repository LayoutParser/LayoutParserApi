# Inventário dos parsers MapperVO — issue #139 (pré-requisito bloqueante da #140)

> Diagnóstico apenas — nenhuma implementação aqui. Escopo: mapear os parsers de
> `MapperVO` existentes no código, comparar estrutura, decidir candidato canônico e
> desenhar o plano de migração que a #140 vai executar.

## 0. Relação com investigação anterior (mesmo tema, sessão diferente)

Existe uma investigação prévia sobre o shape do `MapperVO`, motivada pelo pedido de
`fieldMappings` do front (`docs/architecture/resposta-mapeamento-campo-txt-xml-2026-08-16.md`
e `docs/architecture/plano-execucao-mapeamento-campo-txt-xml-2026-08-16.md`, ambos de
2026-08-16). Ao contrário do que a nomenclatura dos arquivos sugere ("mapeamento campo
TXT↔XML"), o tema central desses dois documentos **é o mesmo `MapperVO`** tratado aqui —
não é o tema de campos de *layout* (`IsStaticValue` em `FieldElement`, tratado à parte no
§2.3 abaixo). Este documento não substitui aqueles — ele **atualiza e formaliza como
inventário técnico** parte do que a `resposta-*` já havia levantado informalmente (§1.1
daquele doc), com duas correções de fato relevantes:

1. **Localização do parser mudou.** A `resposta-*` (2026-08-16) descreve o parser real
   como vivendo em `ai/XslSynth.Core/Core/RealMapperParser.cs` +
   `ai/XslSynth.Core/Model/MapperVo.cs`. Hoje ele vive em
   `ai/XslSynth.Contracts/Core/RealMapperParser.cs` +
   `ai/XslSynth.Contracts/Model/MapperVo.cs` — foi extraído para um projeto de contratos
   sem I/O externo (ver `docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md`
   §1, citado no cabeçalho de `MappingStructureService.cs`).
2. **Já existe um ponto de conexão no runtime da API**, o que a `resposta-*` ainda descrevia
   como "trabalho de integração real, não já existe e só falta plugar" (§2.2 daquele doc):
   `Services/Transformation/MappingStructureService.cs`, registrado no DI
   (`Program.cs:396`), já expõe `ParseRule(XslSynth.Model.MapperRule)` — a assinatura já é
   do parser B (ver §1 abaixo), não do parser A. Ainda **sem consumidor no pipeline HTTP**
   (comentário no próprio arquivo referencia #140/#141 como quem fecha essa ponta) — ou
   seja, o "cano" já está ligado no DI, só falta o caller. Isso não muda o veredito de
   viabilidade da `resposta-*` (ainda não está plugado em `execute-candidates`), mas reduz
   o trabalho de integração restante.

O restante das perguntas em aberto da `resposta-*` (catálogo GUID→XPath, origens N:1 da
DSL, `IsPositionalGroupRepetition`, confirmação do pathway `tcl-xsl`) **continua em aberto**
e é escopo da Fase 2 daquele plano — não é resolvido por este documento, que é focado
estritamente no pré-requisito da #139 (qual parser vira o modelo canônico e como migrar
sem quebrar `MapperDatabaseService`).

## 1. Parsers existentes

| Parser | Arquivos | Namespace/Modelo | Consumidores reais | Profundidade |
|---|---|---|---|---|
| **A — Legado runtime** | `Models/Entities/MapperVo.cs`, `Models/Entities/MapperRule.cs`, `Models/Entities/LinkMappingItem.cs` | `LayoutParserApi.Models.Entities.MapperVo` | `Services/XmlAnalysis/XslGeneratorService.cs:81` (`MapperVo.FromXml(mapDoc)` — único caller real da classe A). **Não** é usado por `MapperDatabaseService.ExtractLayoutGuidsFromDecryptedContent`, ao contrário do que se poderia supor pelo nome "legado runtime" — esse método faz leitura ad-hoc própria (ver §4) | Raso: `Rules`/`LinkMappings` acham elementos de topo só (`ContentValue`, `TargetElementGuid`, GUIDs crus de `InputLayoutGuid`/`TargetLayoutGuid` sem derivar path/leaf/tipo) |
| **B — Real, canônico da síntese XslSynth** | `ai/XslSynth.Contracts/Model/MapperVo.cs` + `ai/XslSynth.Contracts/Core/RealMapperParser.cs` | `XslSynth.Model.MapperVo` (espelhado/desacoplado deliberadamente do runtime Windows-only) | `ai/XslSynth.Core` inteiro (`CoverageValidator`, `DeterministicXslTranspiler`, `LinkMappingTranspiler`, `MapperExtractor`, `ProvenancePublisher`, `RepairOrchestrator`, `FewShotIndex`, `IXslSynthesizer`), o executável `ai/XslSynth/Program.cs` (linhas 849-1160), e na API `Services/Transformation/MappingStructureService.cs:24` (`ParseRule(MapperRule rule)`, tipo já é `XslSynth.Model.MapperRule`) | Mais rico: deriva `TargetType`/`TargetLeafName`/`InputGuid` por convenção (prefixo GUID, sufixo de `Name`) em `LinkMappingItem`; deriva `TargetPath`/`TargetType`/`ParentElement` em `MapperRule` a partir da DSL (`T.<path>=`, regex) |
| **C — Estruturado (downstream de B, Camada 0/1)** | `ai/XslSynth.Contracts/Prompting/StructuredRuleSchema.cs` + `DslStructuredParser` (`ai/XslSynth.Core`) | `StructuredRule`/`StructuredBranch` | `MappingStructureService.ParseRule` produz isso a partir de uma `XslSynth.Model.MapperRule` já parseada por B | Não é um parser de XML alternativo — transformação determinística da DSL (`ContentValue`) de um `MapperRule` já parseado por B. Não compete com A/B como fonte de verdade da estrutura do MapperVO |

## 2. Campos duvidosos — confirmado/refutado por evidência de código

- **`StaticValue`/`IsStaticValue`** — existe no código, mas **não no MapperVO**: é campo de
  **layout** (`Models/Entities/FieldElement.cs:21`, populado por
  `Services/Generation/Implementations/XmlLayoutLoader.cs:150` e
  `Services/Implementations/LayoutParserService .cs:1382`, a partir do XML de *layout*, não
  do *mapper*). Existe também um `StaticValue` homônimo em `StructuredRuleSchema.cs:31`
  (parser C), mas é um literal **derivado** da DSL de uma regra já parseada — não um campo
  lido diretamente do XML do MapperVO. **Nenhum parser de MapperVO (A ou B) lê um campo
  `StaticValue`/`IsStaticValue` do próprio XML do mapper hoje.** Este é exatamente o mesmo
  campo que a `resposta-mapeamento-campo-txt-xml-2026-08-16.md` (§1.1, tabela da §3)
  identificou como confirmado existir no XML real (visto pelo front numa amostra), mas
  ainda não lido por nenhum parser nosso — este inventário confirma que a lacuna
  **persiste**, não mudou desde 2026-08-16.
- **`AcceptEmpty`** — sem nenhuma ocorrência no código sob esse nome exato. Existe
  `AllowEmpty` em A e B (`LinkMappingItem.AllowEmpty`). Hipótese mais provável: é o mesmo
  campo com grafia divergente na pergunta original — a confirmar com quem levantou a
  pergunta antes de tratar como gap real.
- **`IsContainsAttribute`** — zero ocorrências no repositório. Desconhecido, sem uso
  comprovado em nenhum parser, teste ou doc.
- **`IsUseCData`** — zero ocorrências no repositório. Desconhecido, mesma situação.
- **`Elements` aninhados** — nenhum dos dois parsers de MapperVO modela recursão/aninhamento
  de `Rules`/`LinkMappings`; ambos usam `root.Descendants(...)`, produzindo lista achatada.
  Se o MapperVO real tiver grupos aninhados de regras, **nenhum parser atual captura isso**.
  Achado consistente com a pergunta em aberto de `IsPositionalGroupRepetition`/
  `MinimalOccurrence`/`MaximumOccurrence` já registrada na `resposta-*` (2026-08-16, §1.1 e
  tabela §3) — mesma lacuna, ainda não fechada.

### 2.3 Nota de escopo — não confundir com o tema de campos de layout

`plano-execucao-mapeamento-campo-txt-xml-2026-08-16.md` (Fase 1) já formaliza como PBI a
investigação de shape completo do MapperVO de produção, incluindo os mesmos campos
duvidosos acima (`IsStaticValue`, `StaticValue`, `IsPositionalGroupRepetition`,
`MinimalOccurrence`/`MaximumOccurrence`). Este documento **não substitui** aquela Fase 1 —
ele resolve um sub-problema mais restrito e mais urgente (qual dos parsers **existentes**
vira canônico, sem quebrar `MapperDatabaseService`), que é pré-requisito técnico da #140.
A confirmação campo-a-campo contra amostra real de produção continua sendo trabalho da
Fase 1 daquele plano (dono: `@lp-parser-llm`), não deste inventário.

## 3. Avaliação do candidato canônico

**Candidato: `XslSynth.Model.MapperVo` + `RealMapperParser` (Parser B).**

Evidência a favor:
- `MappingStructureService.ParseRule` (já registrado `Scoped` no DI, `Program.cs:396`) foi
  escrito contra o tipo B, não A — a intenção arquitetural de conectar isso ao pipeline HTTP
  (`/fieldMappings`, escopo #140/#141) já pressupõe B como fonte única.
- B é um superconjunto estrutural de A nos campos que ambos capturam (mesmos elementos XML
  lidos, mais os campos derivados por convenção que A não tem).
- B é o parser mais recente e mais testado (`ai/XslSynth.Core.Tests/FewShotIndexStructuredTests.cs`)
  contra estrutura real do MapperVO (comentários no próprio `RealMapperParser.cs` confirmam
  que foi escrito lendo um MapperVO real descriptografado, inclusive tratando o detalhe de
  encoding `utf-16` declarado vs. bytes `utf-8` reais).

Gap identificado entre B e o uso atual de A no `MapperDatabaseService` — ver §4.

## 4. Plano de migração — como conectar B ao `MapperDatabaseService` sem regressão

Critério de aceite mais crítico do dono: **nenhuma regressão no catálogo ou na resolução de
`LayoutGuid`.**

`Services/Database/MapperDatabaseService.cs` (`ExtractLayoutGuidsFromDecryptedContent`,
linhas ~421-470) **não usa nenhum parser de MapperVO hoje** — nem A, nem B. Faz leitura
ad-hoc própria, direto no `XDocument`: busca `InputLayoutGuid`/`TargetLayoutGuid` como
elementos de topo (ou dentro de um elemento `MapperVO`), e só sobrescreve
`mapper.InputLayoutGuid`/`TargetLayoutGuid` **se a coluna do banco já estiver vazia**
(merge condicional, não substituição cega). Esse comportamento específico — e não qualquer
uso de A ou B — é o que está testado e em produção hoje, e é o que não pode regredir.

Passos, em ordem de risco crescente:

1. **Fase de sombra (zero risco).** Rodar `RealMapperParser.Parse` em paralelo, **log-only**
   e sem side effect, sobre o mesmo `XDocument` já parseado em
   `ExtractLayoutGuidsFromDecryptedContent`, comparando o `InputLayoutGuid`/`TargetLayoutGuid`
   que B extrairia contra o que a leitura ad-hoc atual já produz. Critério de avanço:
   divergência zero (ou 100% explicada) numa amostra representativa antes do passo 2.
2. **Migrar o único consumidor real de A.** `XslGeneratorService.cs:81` é o único caller de
   `Models.Entities.MapperVo.FromXml` — trocar para `RealMapperParser`/
   `XslSynth.Model.MapperVo` é o passo de menor risco porque não toca em
   `MapperDatabaseService` nem no critério de aceite crítico.
3. **Deprecar (não remover ainda) o parser A.** `Models/Entities/MapperVo.cs`,
   `MapperRule.cs`, `LinkMappingItem.cs` ficam marcados como obsoletos só depois do passo 2
   confirmado sem regressão por pelo menos um ciclo de QA (`@lp-qa`).
4. **Conectar B ao pathway TCL/XSL real (issue #141, fora de escopo aqui).**
   `MappingStructureService` já tem a assinatura certa — falta só o caller no controller.
   Esse passo depende também da Fase 1 do `plano-execucao-mapeamento-campo-txt-xml-2026-08-16.md`
   confirmar se `tcl-xsl` usa ou não `MapperVO` (pergunta em aberto naquele plano, não
   resolvida aqui).

`ExtractLayoutGuidsFromDecryptedContent` **não é tocado em nenhum destes 4 passos** — o
merge condicional de `InputLayoutGuid`/`TargetLayoutGuid` continua existindo como está até
que a fase de sombra (passo 1) comprove divergência zero, e mesmo assim a decisão de
substituir esse método específico fica fora do escopo desta migração inicial (decisão a ser
tomada separadamente, com validação própria de `@lp-qa`).

## 5. Divisão de trabalho para a implementação (próximo agente)

- **Validável com amostra sintética/sanitizada** (sem risco, qualquer agente): comparar
  saída de A vs. B para um MapperVO sintético cobrindo `LinkMappingItem` + `Rule` simples;
  formato de referência já existe em `ai/XslSynth.Core.Tests/FewShotIndexStructuredTests.cs`.
  Cobre os passos 1 (parcialmente — validação estrutural) e 2 do plano acima.
- **Precisa de amostra real de produção, mantida fora do Git** (não deve ser obtida por
  nenhum agente diretamente): confirmar existência de `Elements` aninhados não capturados
  por nenhum parser, e confirmar/refutar `IsContainsAttribute`/`IsUseCData`/`AcceptEmpty`
  contra um MapperVO real. Só o dono (ou quem tem acesso ao banco/`.exe` de descriptografia)
  pode rodar essa comparação localmente, sem commitar nenhum artefato — mesma restrição já
  registrada na Fase 1 do `plano-execucao-mapeamento-campo-txt-xml-2026-08-16.md`. A fase de
  sombra do passo 1 acima (divergência zero entre `RealMapperParser` e a leitura ad-hoc do
  `MapperDatabaseService`) também exige essa amostra real — não é validável só com dado
  sintético, porque o objetivo é justamente detectar divergência em casos reais de produção.

## 6. Referências cruzadas

- `docs/architecture/resposta-mapeamento-campo-txt-xml-2026-08-16.md` — investigação
  original do shape do MapperVO, motivada pelo pedido `fieldMappings` do front; superset de
  perguntas em aberto (catálogo GUID→XPath, origens N:1 da DSL, granularidade de grupo
  repetido, pathway `tcl-xsl`) que este documento não resolve.
- `docs/architecture/plano-execucao-mapeamento-campo-txt-xml-2026-08-16.md` — plano de 3
  fases + Fase 0 daquela investigação; a Fase 1 ("confirmar shape real do MapperVO de
  produção") é o complemento direto deste inventário, com escopo mais amplo (todos os
  campos, não só a escolha do parser canônico).
- `docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md` §1 — decisão
  de extrair `RealMapperParser`/`MapperVo` para `ai/XslSynth.Contracts` (motivo da correção
  de localização no §0 acima).

## 7. Status de implementação (issue #139, atualizado nesta sessão)

Dos 4 passos do plano de migração (§4), os passos **1-3 estão concluídos** nesta sessão
(branch `feat/mapper-vo-parser-comparacao-139`). O passo **4 permanece fora de escopo**,
como já previsto — é trabalho das issues #140/#141.

- **Passo 1 (fase de sombra) — concluído.** `MapperDatabaseService.ExtractLayoutGuidsFromDecryptedContent`
  chama `CompareWithRealMapperParserShadow(mapper, doc)` (`Services/Database/MapperDatabaseService.cs`,
  em torno da linha 475) logo após a extração ad-hoc de `InputLayoutGuid`/`TargetLayoutGuid`.
  É **log-only**: roda `RealMapperParser` sobre o mesmo `XDocument` já parseado e compara o
  resultado contra a leitura ad-hoc existente, sem nenhum side effect e sem alterar a fonte
  de verdade atual (o merge condicional descrito no §4 continua intocado). Ainda precisa
  acumular evidência de divergência zero contra amostra real de produção antes de qualquer
  decisão de substituir a leitura ad-hoc — isso continua fora do alcance de qualquer agente
  (ver §5, restrição de amostra real fora do Git).
- **Passo 2 (migrar o único consumidor real de A) — concluído.** `Services/XmlAnalysis/XslGeneratorService.cs`
  (em torno da linha 82-90) agora chama `new RealMapperParser().Parse(mapDoc)` em vez de
  `Models.Entities.MapperVo.FromXml(mapDoc)`, com fallback para o XML bruto e `LogWarning`
  em caso de falha de parse — coerente com o padrão de resiliência do projeto
  (`.claude/rules/dotnet-standards.md` §"Resiliência").
- **Passo 3 (deprecar o parser A) — concluído.** `Models/Entities/MapperVo.cs`,
  `MapperRule.cs` e `LinkMappingItem.cs` estão marcados `[Obsolete("...")]`, cada atributo
  apontando de volta para este documento — não foram removidos (o histórico de uso continua
  rastreável, e a remoção física é decisão futura, não parte deste passo).
- **Passo 4 (conectar B ao pathway TCL/XSL real, controller HTTP) — pendente, fora de
  escopo.** Ver issues #140/#141; `MappingStructureService` já expõe a assinatura certa
  (§0, item 2) mas segue sem consumidor HTTP.

**Limitação confirmada nesta sessão:** nenhum dos parsers (A legado nem B/`RealMapperParser`,
canônico) captura elementos `Elements` aninhados de `Rules`/`LinkMappings` — ambos usam
`root.Descendants(...)`, que produz lista achatada independentemente de profundidade no XML
de origem. Isso já estava registrado como achado no §2 acima; fica reafirmado aqui como
limitação que **sobrevive à migração completa dos passos 1-3** (trocar A por B não resolve
esse gap, porque nenhum dos dois modela recursão). Confirmar/mitigar aninhamento real
depende de amostra de produção (mesma restrição do §5) e é trabalho de uma sessão futura,
não coberto pelos passos 1-3 concluídos aqui.
