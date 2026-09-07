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
