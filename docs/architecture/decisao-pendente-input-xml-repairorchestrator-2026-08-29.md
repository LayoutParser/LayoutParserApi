# Decisão pendente — de onde vem o `input XML` do `RepairOrchestrator` (2026-08-29)

## Gap confirmado (achado da Quinn, investigado por Lia)

`RepairOrchestratorXslSynthesizerService.SynthesizeAsync` só é chamado a partir de
`AiCandidateDispatchPlan.TryBuild`, que só constrói o plano quando `isXmlInput == false`
(linha 27-28) — ou seja, a entrada é sempre **TXT posicional cru**. Mas o método faz
`XDocument.Parse(inputXml)` esperando XML bem-formado — sempre lança, sempre degrada para o
loop legado (`RunLoopAsync` → fallback XML-direto via Ollama). O motor novo é código morto no
único ponto onde é chamado hoje, em `0c4ccb9`.

## Investigação: existe XML intermediário reaproveitável?

Não. `LowCodeAutoTransformationService.TransformSingleAndPersistAsync` chama
`LowCodeTransformationService.TransformAsync(txtContent, mapperId, ...)`, que é uma
**caixa-preta opaca**: escreve o TXT num arquivo temporário, invoca o `.exe` x86 do Sysmiddle
(`LowCode:RunnerPath`) via `Process.Start`, e lê **só o XML final** (`out_*.xml`) de volta. Não
existe passo intermediário exposto entre "TXT cru" e "XML final já transformado pelo XSLT/DSL
do mapeador" — o runner faz os dois passos (parse posicional → aplicar XSLT do mapeador) dentro
do processo `.exe`, sem hook de saída no meio.

Também não existe, em `Services/Parsing/`, nenhum conversor genérico TXT-posicional→XML
independente do runner Sysmiddle (confirmado por busca; `Services/Transformation/
StructuralResolution/` — issue #140 — resolve GUID→XPath para compor `fieldMappings`/
`sectionMappings` do **XML final**, não gera um XML de entrada pré-XSLT).

## Por que isso é maior que "conectar dois pontos existentes"

`RepairOrchestrator.RunAsync` espera um `XDocument input` que é a **entrada da própria XSLT
sintetizada** (`_applier.Apply(xslt, input)`) — ou seja, precisa estar no mesmo dialeto XML que
`LinkMappingItem.SourcePath` referencia (comentário no código: `origem (<InputLayoutGuid>)`),
não o TXT cru nem o XML final do pathway sysmiddle. Não há hoje, em lugar nenhum do projeto, um
componente que produza esse XML de entrada sem passar pelo `.exe` opaco do Sysmiddle (que já
entrega o resultado FINAL, pulando esse estágio).

Duas saídas possíveis, ambas fora do escopo de "conectar dois pontos":

1. **Construir um conversor TXT→XML genérico novo** (parser posicional → XML no dialeto que os
   `LinkMappings`/`SourcePath` esperam) — trabalho de parsing novo, não wiring.
2. **Mudar a assinatura pública do `RepairOrchestrator`** para aceitar o resultado estruturado
   do parse (`ParsedField`/`LayoutVO`) em vez de `XDocument input` pronto — mudança na
   `ai/XslSynth.Core`, que também precisa continuar funcionando standalone via CLI
   (`ai/XslSynth/Program.cs`), fora do risco que a tarefa pediu para evitar.

## O que NÃO foi feito nesta sessão (deliberado)

Não implementei nenhuma das duas saídas acima — nem um hack de "usar o XML final do sysmiddle
como input" (seria uma transformação identidade contra o próprio gabarito, convergindo
trivialmente sem sintetizar nada real, mascarando a métrica de sucesso do motor). Reportando o
bloqueio em vez de arriscar `XslSynth.Core`/CLI, conforme instrução da tarefa.

## Próximo passo recomendado

Escalar a decisão de design a `@lp-architect` (Aria): qual das duas saídas (conversor novo vs.
assinatura nova do orquestrador) é o caminho certo, considerando que `ai/XslSynth.Core` também
serve o CLI standalone e não pode quebrar. Só depois disso o `RepairOrchestrator` tem uma chance
real de disparar no fluxo de produção.

---

## Decisão (Aria, 2026-08-29)

**Opção 1 — conversor TXT→XML novo, independente do `.exe`.** Não como trabalho do zero: já
existem **dois precedentes reais no próprio `ai/XslSynth`** que fazem exatamente esse papel,
sem tocar o Sysmiddle:

- `Excel/RootTreeBuilder.cs` — TXT posicional MQSeries + `SpecModel` (da planilha `.xlsx`) → árvore ROOT.
- `Metrics/TclRootBuilder.cs` — TXT de instância + schema `<MAP>` TCL → ROOT, comentário no
  código já descreve isso como *"o documento de entrada esperado pelo XSLT gerado"* — ou seja,
  já é literalmente o input que o `RepairOrchestrator` precisa, só que hoje alimentado por um
  schema TCL avulso do dataset em vez do `LayoutVO`/`ParsedField` real do parser da API.

Isso muda a pergunta de "construir do zero" para "generalizar um padrão já testado": trocar a
fonte de estrutura (planilha ou TCL avulso) pelo `LayoutVO`/`ParsedField` que o parser posicional
da API já produz em runtime. Confirma também que o CLI **não** aponta pra Opção 2: nem
`RunSampleAsync` (usa fixture estática `sample/input.xml`) nem `RunRealAsync` (nem chama
`RepairOrchestrator` — monta o candidato via `CandidateBuilder` sem gabarito de runtime) fornecem
hoje um `XDocument input` real derivado de parsing em produção. Não há sinal de que a assinatura
`XDocument`-first do orquestrador seja artificial — é, na prática, não exercitada ainda.

### Por que não Opção 2

Mudar a assinatura pública do `RepairOrchestrator` para aceitar `ParsedField`/`LayoutVO`
empurraria a conversão pra dentro do `XslSynth.Core`, que **também** é consumido pelo CLI
standalone (que roda fora do processo da API, sem acesso aos tipos de domínio do parser). Isso
acopla um projeto deliberadamente isolado (ver `xslsynth-trilha-a-overlap` na memória da Aria) ao
modelo de domínio da API — na direção errada do boundary (`.claude/CLAUDE.md` §"boundary dos
repos": lógica de runtime fica na API). A Opção 1 mantém `RepairOrchestrator` agnóstico de onde o
`XDocument` veio, que é a forma certa de um motor de síntese reutilizável.

### O dialeto XML de entrada

Não é o XML final da Sysmiddle nem um XML genérico trivial: é o ROOT hierárquico que os
`LinkMappings`/`SourcePath` do mapeador referenciam (comentário no código: `origem
(<InputLayoutGuid>)`) — precisa refletir a estrutura declarada do `LayoutVO` de origem (linhas/
campos, e hierarquia quando declarada via `CHILD`/repetição de grupo). `TclRootBuilder` já
resolve esse problema com um gate de qualidade importante a preservar: **recusa produzir um ROOT
quando a taxa de casamento entre linhas do TXT e identificadores do schema é baixa**, em vez de
devolver um ROOT vazio que mascararia como XML "válido" rio abaixo. Esse princípio deve migrar
junto pro conversor novo.

### Passo a passo para `@lp-parser-llm` executar

1. Criar `Services/Parsing/ParsedFieldToRootXmlConverter.cs` (ou local equivalente em
   `ai/XslSynth.Core`, a decidir pelo ponto de entrada real — ver item 4) que recebe
   `IReadOnlyList<ParsedField>` (ou `LayoutVO` completo, o que já estiver disponível no ponto de
   chamada) e produz um `XDocument` ROOT, seguindo o mesmo padrão estrutural de
   `TclRootBuilder.BuildRootFromInstance` (nomes/hierarquia derivados do layout, não hardcoded).
2. Portar o gate de taxa de casamento (`TaxaMinimaCasamento = 0.90` e a contagem de tipos de
   registro distintos) — recusar e sinalizar motivo em vez de gerar ROOT parcial/vazio.
3. Ligar esse conversor no ponto onde `AiCandidateDispatchPlan.TryBuild` hoje decide
   `isXmlInput == false` (fluxo TXT posicional) — é aí que o `RepairOrchestrator` precisa do
   `XDocument input` e hoje falha com `XDocument.Parse` sobre TXT cru.
4. Decidir a localização do novo conversor com base em uma pergunta técnica simples: o tipo
   `ParsedField`/`LayoutVO` é acessível a partir de `ai/XslSynth.Core` sem referência circular?
   Se sim, o conversor pode viver lá (ao lado de `RootTreeBuilder`/`TclRootBuilder`, reduzindo
   duplicação). Se não (mais provável, dado o isolamento deliberado do projeto), o conversor vive
   em `Services/Parsing/` na API e `RepairOrchestratorXslSynthesizerService` monta o `XDocument`
   ali antes de chamar `XslSynth.Core` — a API continua dona da conversão de domínio,
   `XslSynth.Core` continua agnóstico.
5. Escrever um teste de regressão comparando o ROOT gerado por este conversor contra o ROOT
   gerado por `TclRootBuilder` para o mesmo par TXT+schema do dataset de métricas (par real já
   existe no corpus) — não pra bater 100%, mas pra validar que a estrutura hierárquica básica
   (linhas → campos → filhos) é equivalente antes de trocar a fonte de dados em produção.

Nenhum código foi implementado nesta sessão (fora de escopo da Aria).
