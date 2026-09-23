# Resultado do spike A+B — Reconstrução reversa XML→TXT (Issue #151)

Autor: `@lp-parser-llm` (Lia). Escopo aprovado pelo dono em 2026-09-07 (comentário na issue #151):
Fase A (metadado `Reversible` no `FunctionCatalog`) + Fase B (generalizar
`FieldToXmlMappingComposer` para `Direction: Forward|Reverse`), medindo contra a DLL real
Sysmiddle vendorizada no repo — sem prometer Fase C/D até ver o número.

Design de referência: `design-reconstrucao-reversa-xml-txt-2026-09-03.md`.

## 1. O que foi implementado

### Fase A — metadado `Reversible` por função

- `ai/XslSynth.Contracts/Prompting/FunctionCatalog.cs`: `FunctionCatalogEntry` ganhou
  `bool Reversible` + `string? IrreversibilityReason`, default conservador
  (`false`, "sem curadoria manual") quando uma classe `*Function` não tem entrada curada.
- `ai/XslSynth.Contracts/Prompting/FunctionReversibilityCatalog.cs` (novo): curadoria manual,
  chaveada pelo **nome de classe real** (ex.: `"ConcatFunction"`, não o nome-palpite pós-
  `GuessDslName`), com motivo textual em cada entrada. 175 entradas — cobrindo as 173 classes
  reais confirmadas por reflection na DLL vendorizada (2 nomes na curadoria não bateram com
  nenhuma classe real, harmless).
- **Bug real corrigido durante o spike:** `FunctionCatalog.ExtractFromDll` montava o
  `MetadataLoadContext`/`PathAssemblyResolver` só com as DLLs do runtime .NET + a própria DLL de
  funções — sem incluir `SysMiddle.Base.dll` (onde mora `FunctionMember`, a base checada por
  `InheritsFrom`) nem as demais DLLs Sysmiddle vizinhas. Sem elas, `GetTypes()` falhava para
  **todos** os tipos e o catálogo saía vazio silenciosamente (nunca detectado antes porque o
  único teste que exercitava a DLL real gate-ava em `.claude/tmp/sysmiddle/`, caminho que nunca
  existiu nas máquinas onde os testes rodaram até agora). Corrigido incluindo as DLLs do mesmo
  diretório do alvo no resolver — ainda 100% reflection-only, não executa nenhum código.

### Fase B — `Direction: Forward | Reverse`

- `ai/XslSynth.Contracts/Model/StructuralResolutionModels.cs`: novo enum `Direction`
  (`Forward`/`Reverse`); `FieldToXmlMapping` ganhou `Direction Direction = Direction.Forward`.
- `ai/XslSynth.Contracts/Core/StructuralResolution/FieldToXmlMappingComposer.cs`:
  `MappingCandidate` ganhou `Direction Direction = Direction.Forward` e
  `BranchReversibility? Reversibility = null`. `Compose(...)` ganhou uma **6ª condição**,
  só relevante em `Reverse`: a reconstrução só é `Authoritative` se a função/branch envolvida for
  confirmada bijetora (Fase A). Em `Forward`, a condição é trivialmente satisfeita — 100% do
  comportamento de #140/#141 preservado (todos os 12 testes originais de
  `FieldToXmlMappingComposerTests` continuam passando sem alteração).
- `ai/XslSynth.Contracts/Core/StructuralResolution/BranchReversibilityResolver.cs` (novo): resolve
  a reversibilidade de um `StructuredBranch` inteiro a partir das funções que ele referencia —
  sem função = sempre reversível (cópia posicional); com função = reversível só se **todas** as
  funções referenciadas forem `Reversible = true` no catálogo curado (uma função com perda torna
  o branch inteiro não-reversível, mesmo com outras bijetoras).
- **Deliberadamente fora de escopo** (confirmado pelo pedido do dono): `OccurrenceResolver`
  simétrico completo (XML-ocorrência → linha física do TXT) e qualquer serviço/endpoint novo —
  isso é Fase C/D.

## 2. Medição real (não estimativa)

Executada em `ai/XslSynth.Core.Tests/SpikeReversibilidadeMeasurementTests.cs`, contra
`tools/LowCodeRunner/Functions/SysMiddle.ConnectUs.Functions.dll` (DLL Sysmiddle real, já
versionada no repo — mesma usada pelo LowCode runner em produção). Reflection-only
(`MetadataLoadContext`), sem executar nenhum código da DLL.

```
Total de funções extraídas (reflection, SysMiddle.ConnectUs.Functions.dll): 173
Reversíveis (curadoria Fase A): 23 (13,3%)
Não-reversíveis (curadoria Fase A): 150
Sem curadoria manual (default conservador): 0
```

As 23 funções reversíveis: `CDataString`, `ConvertFromBase64Bytes`, `ConvertFromBase64String`,
`ConvertToBase64Bytes`, `ConvertToBase64String`, `ConvertToChar`, `DecompressBytes`, `False`,
`GetBrazilianCityCode`, `GetBrazilianStateCodeByInitials`, `GetBrazilianStateCodeByName`,
`GetBytes`, `GetSplittedValueByPosition`, `GetStringFromBytes`, `PadLeft`, `PadRight`,
`SplitValueByPosition`, `Trim`, `TrimEnd`, `TrimStart`, `True`, `UriEscape`, `UriUnescape`.

**Leitura honesta do número:** 13,3% é a fração de funções *individualmente* bijetoras no
catálogo completo de 173 — não é a fração de **regras de mapeamento reais do corpus de produção**
que seriam reversíveis. As duas coisas divergem em dois sentidos:

1. **Para pior:** funções puramente estruturais (sem função nenhuma, `MappingKind.Direct`) são
   sempre reversíveis e não aparecem nesta contagem — muitos campos do mapeamento real não usam
   função alguma, então a taxa real de campos reversíveis tende a ser **maior** que 13,3%.
2. **Para pior ainda:** a curadoria conta ~40% do catálogo (70 das 173 classes) como funções de
   I/O/efeito colateral/ambiente (banco, arquivo, e-mail, processo) que **nunca** deveriam
   aparecer numa regra de transformação de campo fiscal — não é realista que essas funções sejam
   usadas nas regras `Rules`/`StructuredBranch` reais do domínio NF-e/CT-e. Elas inflam o
   denominador sem representar risco real.
3. **Para melhor (risco real):** a função mais citada no critério de aceite da própria issue
   #151 (`CalculateVerifierDigit`) e a mais estruturalmente central (`Concat`/`ConcatString`,
   base de `MappingKind.Concatenated`) estão **ambas** na lista de não-reversíveis — são,
   supostamente, duas das funções mais usadas em regras fiscais reais (dígito verificador de
   chave de acesso, concatenação de campos). Sem acesso ao corpus real de `Rules`/
   `StructuredBranch` de produção nesta sessão (não disponível no ambiente — ver §3), não foi
   possível medir a frequência de uso real dessas funções especificamente, que é o dado que
   realmente decide o ROI de C/D.

## 3. Limitação da medição (transparência sobre o que NÃO foi possível medir)

O pedido original do spike era medir contra "uma amostra real do corpus" de pares `Rules`/
`StructuredBranch` de produção. Essa amostra **não estava disponível no ambiente desta sessão**
(diretórios de corpus mencionados em memória de sessões anteriores — `.claude/tmp/exemplos`,
datasets de fine-tuning — não existem neste ambiente). A medição real que foi possível é sobre o
**catálogo de funções em si** (população de 173 classes da DLL real), não sobre a frequência de
uso dessas funções nas regras de produção. Isso é uma limitação real da medição, não um resultado
inflado — o número de 13,3% caracteriza "quão bijetora é a biblioteca de funções Sysmiddle como
um todo", não "quão reversível é o corpus de mapeamentos fiscais reais".

## 4. Recomendação

**Não avançar direto para Fase C/D com este número isolado.** A medição de população de funções
(13,3%) é baixa, mas é um indicador fraco do ROI real — o que decide o ROI de C/D é a frequência
de uso das ~10-15 funções mais comuns em `Rules`/`StructuredBranch` de produção, não a fração do
catálogo inteiro (que é dominado por funções de I/O irrelevantes ao domínio fiscal). O sinal mais
concreto encontrado neste spike é qualitativo, não quantitativo: as duas funções citadas no
próprio critério de aceite da issue (`CalculateVerifierDigit`, `Concat`) — supostamente centrais
em regras fiscais reais — são ambas não-reversíveis, o que sugere que o **caminho puro** ("reverter
a regra sem o TXT original") vai cobrir uma fração pequena dos campos reais mesmo que o resto do
catálogo fosse 100% reversível.

**Próximo passo recomendado, antes de comprometer Fase C/D:** medir a frequência de uso real de
`CalculateVerifierDigit`/`ConcatString`/demais funções nas `Rules` de produção (não disponível
nesta sessão) — se essas duas dominarem o uso real, o valor de negócio de C/D cai ainda mais,
reforçando a estratégia já registrada no design (§1): quando o TXT original existir na sessão do
usuário, usá-lo como fonte de verdade e a regra reversível como **validação**, não como única via
de reconstrução. Fase C/D só valem a pena isoladamente (sem o TXT original disponível) se essa
medição de frequência real mostrar uso majoritário de funções estruturais/bijetoras — o que este
spike não conseguiu confirmar nem refutar por falta de acesso ao corpus real nesta sessão.

## 5. Arquivos alterados

- `ai/XslSynth.Contracts/Prompting/FunctionCatalog.cs` — metadado `Reversible`/`IrreversibilityReason` + fix do resolver de assemblies.
- `ai/XslSynth.Contracts/Prompting/FunctionReversibilityCatalog.cs` (novo) — curadoria manual.
- `ai/XslSynth.Contracts/Model/StructuralResolutionModels.cs` — enum `Direction`, `FieldToXmlMapping.Direction`.
- `ai/XslSynth.Contracts/Core/StructuralResolution/FieldToXmlMappingComposer.cs` — `MappingCandidate.Direction`/`Reversibility`, condição 6.
- `ai/XslSynth.Contracts/Core/StructuralResolution/BranchReversibilityResolver.cs` (novo).
- `ai/XslSynth.Core.Tests/FunctionReversibilityCatalogTests.cs` (novo).
- `ai/XslSynth.Core.Tests/StructuralResolution/BranchReversibilityResolverTests.cs` (novo).
- `ai/XslSynth.Core.Tests/StructuralResolution/FieldToXmlMappingComposerTests.cs` — 5 casos novos de `Direction.Reverse`.
- `ai/XslSynth.Core.Tests/SpikeReversibilidadeMeasurementTests.cs` (novo) — instrumento de medição, roda contra a DLL real vendorizada.

`dotnet build`/`dotnet test` (solução completa e `ai/XslSynth.Core.Tests` isolado) limpos — 80/80
testes verdes em `XslSynth.Core.Tests`, incluindo os 15 novos desta issue.

## Refinamento por engine (TCL/XSLT) — 2026-09-07

Pedido do dono: a reconstrução reversa importa, na prática, só para transformações publicadas
nas engines **TCL** e **XSL/XSLT** (`MappingRelease.Engine ∈ {"tcl","xslt"}`, Slice 5 / issue
#231 — `Services/Fiscal/MappingCompileService.cs`, `Services/Fiscal/MappingDraftRuleTranspiler.cs`).
Sysmiddle é read-only e fora de escopo por definição (#200/#205). A medição da §2 acima foi feita
contra o catálogo de **173 funções do Sysmiddle** — universo errado para essa pergunta. Este
refinamento troca o universo de medição para o que as engines TCL/XSLT realmente usam.

### Achado principal: TCL/XSLT (Slice 5) não usa o catálogo de 173 funções do Sysmiddle

Lendo `MappingDraftRuleTranspiler.cs` (gerador determinístico de ambas as engines, sem LLM) e
`FiscalMappingRuleExtractor.cs` (fonte das regras, extraídas de planilha .xlsx de autoria fiscal,
não do Sysmiddle) ponta a ponta: **as engines TCL e XSLT deste repositório são um subsistema novo
e auto-contido (issue #231), sem qualquer dependência do `FunctionCatalog`/
`FunctionReversibilityCatalog` do Sysmiddle.** O universo de operações é fechado e pequeno:

```csharp
// Services/Fiscal/MappingDraftRuleTranspiler.cs:44
private static readonly HashSet<string> SupportedOperations = new(StringComparer.OrdinalIgnoreCase)
    { "copy", "concat", "lookup", "conditional", "constant" };
```

Toda `MappingDraftRule` fora dessas 5 operações vira diagnóstico `error` ("fora do catálogo
determinístico suportado") e nunca chega a ser emitida como TCL (`BuildTclField`) ou XSLT
(`BuildXsltRuleElement`) — ou seja, **é estruturalmente impossível** uma regra publicada em TCL/
XSLT usar `CalculateVerifierDigit`, `ConcatString` (a função Sysmiddle) ou qualquer uma das outras
172 funções do catálogo antigo. Essas funções pertencem exclusivamente à engine `"sysmiddle"`
(`SysmiddleExplanationAdapter.cs`), que é read-only e já está fora de escopo por decisão prévia
(#200/#205). A `"concat"` que existe em TCL/XSLT é uma operação própria do transpiler (gera
`concat(...)` XPath ou `campo1+campo2` em TCL a partir de `SourceRefs`) — sintaticamente parecida
mas semanticamente independente da função `ConcatString` do Sysmiddle medida na §2.

**Consequência direta para as perguntas 1 e 2 do pedido:** `CalculateVerifierDigit` tem frequência
**zero** garantida no universo TCL/XSLT — não é um "achado de frequência baixa", é impossibilidade
estrutural (a operação nem está em `SupportedOperations`). `Concat`/`ConcatString` também não
aparecem — o que existe é a operação `"concat"` do transpiler, avaliada separadamente abaixo.

### Reversibilidade das 5 operações (análise estrutural — universo fechado, não amostral)

Como o universo tem só 5 operações (não 173), dá para analisar reversibilidade de cada uma
individualmente em vez de amostrar frequência de uso:

| Operação | Reversível? | Motivo |
|----------|-------------|--------|
| `copy` | **Sim** (bijetora) | `<xsl:value-of select="sourceRef"/>` / TCL `source direto` — cópia 1:1 sem transformação, mesmo padrão de `MappingKind.Direct` já tratado como reversível em Fase B. |
| `lookup` | **Condicional** | Reversível só se a tabela (`ReadLookupTable`) for injetora (nenhum valor de destino repetido para chaves diferentes) — precisaria da mesma curadoria manual por regra que `FunctionReversibilityCatalog` já faz para funções Sysmiddle, só que aplicada a `lookup.Table` em vez de classe de função. Tabelas fiscais reais (UF→código, por ex.) tendem a ser injetoras, mas isso não foi confirmado contra dado publicado real (ver limitação abaixo). |
| `concat` | **Parcial/condicional** | Reversível (splitável) só se houver separador não-vazio e esse separador não ocorrer dentro dos valores de origem — caso contrário a junção é ambígua (`"AB"+"C"` vs `"A"+"BC"` sem separador são indistinguíveis). Sem acesso às regras reais publicadas, não dá pra saber que fração usa separador seguro. |
| `conditional` | **Não, em geral** | O `<xsl:choose>`/`|`-list gerado não preserva qual `test` foi avaliado — só o valor resultante. Se dois ramos puderem produzir o mesmo valor de saída a partir de entradas diferentes (comum em regras fiscais com fallback/default), a reconstrução é ambígua. |
| `constant` | **Não** | Por definição descarta o campo de origem — não há entrada pra reconstruir (o valor é fixo, independente do TXT original). |

Sem contar `lookup`/`concat` como reversíveis (caso conservador, exigindo curadoria que não foi
feita): **1 de 5 operações claramente reversível (20%)** — mais alto que os 13,3% da medição
antiga, mas por um motivo diferente: aqui não há as ~70 funções de I/O/efeito colateral que
inflavam o denominador do catálogo Sysmiddle; o universo é pequeno e cada item pesa mais.
Contando `lookup` como potencialmente reversível com curadoria (caso otimista): até **2 de 5
(40%)**. Isso é uma cota estrutural teórica, não uma medição de uso real — ver limitação abaixo.

### Medição de frequência real: BLOQUEADA por falta de dado real nesta sessão

Tentativas concretas nesta sessão, todas sem sucesso:

1. **Banco de dados real** (`tbMappingRelease`, `IdentityDatabase:*`, `172.25.32.5,1433`,
   `Services/Database/SqlMappingReleaseStore.cs`) — teria a query natural
   (`SELECT Engine, ArtifactsJson FROM tbMappingRelease WHERE Status = 'published' AND Engine IN
   ('tcl','xslt')`). Sem credencial disponível nesta sessão: `dotnet user-secrets list` só expõe
   `Database:Password` (o SQL antigo compartilhado, read-only por regra própria — não o
   `IdentityDatabase:*` deste subsistema); nenhuma env var `IdentityDatabase__*` presente.
2. **Corpus de fixture local** — não há `.xlsx`/pacote real de `FiscalMappingRuleExtractor` no
   repo (a extração é feita a partir de planilha fornecida pelo dono, não versionada); não há
   export de `MappingDraftRule`/`MappingRelease` real em `.claude/tmp/` (diretório não existe
   nesta sessão) nem em fixtures de teste com dado "de produção" (os testes de
   `MappingDraftRuleTranspiler`/`MappingCompileService` usam regras sintéticas, não reais).
3. O dataset real mais próximo disponível localmente (`ai/XslSynth/training-data/sysmiddle-dsl-
   dataset-2026-09-02.jsonl`, 6044 exemplos reais extraídos de mapeadores Sysmiddle de produção)
   **não serve para esta pergunta** — é DSL da engine `"sysmiddle"` (fora de escopo), não TCL/XSLT
   do Slice 5. Confirmado grep: o dataset contém `CalculateVerifierDigit`/`ConcatString`
   fartamente, mas isso só reforça que essas funções vivem no subsistema errado para esta medição.

**Não foi possível medir frequência de uso real das 5 operações em releases publicadas de TCL/
XSLT.** É plausível que o volume publicado seja pequeno ou zero — o Slice 5 (issue #231) é recente
— mas isso não foi confirmado (exigiria acesso ao `IdentityDatabase` real ou export do dono).
Reportar isso como bloqueio de dado, não como "poucas funções não-reversíveis" — não inventar número.

### Recomendação atualizada

O achado mais importante deste refinamento **não é um número de frequência** — é que **a pergunta
original do spike A/B mediu o subsistema errado**. Para o escopo real de #151 (TCL/XSLT, Slice 5):

1. **`CalculateVerifierDigit`/`ConcatString` são não-questões neste escopo** — não podem aparecer
   em regra TCL/XSLT publicada, o transpiler as bloqueia estruturalmente. A preocupação levantada
   no critério de aceite da issue (essas duas funções) se aplica ao Sysmiddle, não ao Slice 5.
2. **O problema de reversibilidade em TCL/XSLT é bem menor e mais tratável**: 5 operações, não
   173. Fase C/D (se avançar) deveria implementar reversibilidade **nessas 5 operações do
   `MappingDraftRuleTranspiler`**, não reaproveitar `FunctionReversibilityCatalog`/
   `FunctionCatalog` do Sysmiddle — são universos de código diferentes.
3. **Antes de comprometer Fase C/D, ainda falta o número real**: qual fração das regras
   publicadas é `copy` (reversível de graça) vs `concat`/`lookup`/`conditional`/`constant`. Por
   design de UI/autoria assistida (issue #103, planilha de decisão fiscal → regra), a expectativa
   qualitativa é que `conditional`/`lookup` sejam relativamente comuns (é o propósito das abas de
   "tabela de decisão" que `FiscalMappingRuleExtractor` extrai) — o que empurraria a fração
   reversível para perto do piso de 20%, não do teto de 40%. Mas isso é uma hipótese qualitativa,
   não medição.
4. **Próximo passo concreto**: pedir ao `@lp-devops` acesso de leitura ao `IdentityDatabase`
   (172.25.32.5,1433, `LayoutParserIdentity`) para rodar a query real contra `tbMappingRelease
   WHERE Status = 'published' AND Engine IN ('tcl','xslt')` e contar `Operation` por
   `MappingDraftRule` das releases publicadas — ou, se ainda não há releases publicadas em
   produção, aguardar volume real antes de investir em Fase C/D (não vale medir contra dado
   sintético/de teste como se fosse produção).

**Veredito para o dono:** não avançar Fase C/D ainda, mas por motivo diferente do spike original —
não é "baixa taxa de reversibilidade" (essa taxa, no universo certo, é estruturalmente mais alta:
20-40% vs 13,3%), é **falta de acesso a dado publicado real** para saber se vale o investimento.
Medição bloqueada por dado, não por inviabilidade.

### Arquivos lidos/analisados neste refinamento (sem alteração de código)

- `Services/Fiscal/MappingDraftRuleTranspiler.cs` — `SupportedOperations`, `BuildXsltRuleElement`,
  `BuildTclField`, `BuildCopy/Concat/Lookup/Conditional/Constant`.
- `Services/Fiscal/MappingCompileService.cs`, `Services/Fiscal/TclExplanationAdapter.cs`,
  `Services/Fiscal/XsltExplanationAdapter.cs`, `Services/Fiscal/SysmiddleExplanationAdapter.cs` —
  confirmação dos 3 valores de `Engine` (`"tcl"`, `"xslt"`, `"sysmiddle"`) e que só o primeiro par
  está em escopo.
- `Services/Database/SqlMappingReleaseStore.cs`, `Services/Interfaces/IMappingReleaseStore.cs` —
  confirmação de que `tbMappingRelease` é o dado real a consultar, e que exige `IdentityDatabase:*`
  (indisponível nesta sessão).
- `Services/Fiscal/FiscalMappingRuleExtractor.cs` — confirmação de que não há corpus real
  versionado no repo (extração parte de `.xlsx` fornecido pelo dono, fora do controle de versão).
