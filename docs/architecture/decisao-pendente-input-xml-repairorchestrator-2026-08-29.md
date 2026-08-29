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
