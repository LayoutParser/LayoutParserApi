# Gap real: Ollama gera TCL/XSL de verdade? (2026-08-21)

## Veredito

**Parcial, e não do jeito que o README §5 descreve.** O loop RAG→gerar→validar(XSD)→corrigir
**roda em produção, automaticamente, via Ollama** — isso é real e confirmado no código
(`Services/Transformation/Ai/AiTransformationCandidateService.cs`, DI em `Program.cs:441-442`,
disparado por `TransformationExecutionController` quando `execute-candidates` não produz
candidato). Mas ele **não gera TCL nem XSLT** — ele pede ao Ollama pra gerar o **XML final
diretamente** a partir do TXT de entrada (com ou sem gabarito), via `/api/generate`, e valida o
resultado com XSD + diff canônico. É tradução direta TXT→XML-final, não síntese de um artefato de
transformação reutilizável.

O componente que de fato sintetiza **XSLT** (`ai/XslSynth.Core/Core/RepairOrchestrator.cs`, com
`OllamaXslSynthesizer`, `CanonicalDiffer`, loop gerar→validar→corrigir sobre `MapperVo`) existe,
é sofisticado e é referenciado **só** por `ai/XslSynth/Program.cs` (CLI standalone, Linux/WSL,
fora do build da API). Zero chamada a partir do runtime da API. Não existe, em lugar nenhum do
código atual, geração de **TCL** via IA — `TclGeneratorService`/`ImprovedTclGeneratorService`
existem mas são geração determinística baseada em regras, não LLM.

## Classificação

### Já funciona hoje, ponta a ponta, no runtime real da API
- **Fallback automático de IA em `execute-candidates`** (Estado A/B, `AiTransformationCandidateService`):
  gera XML final direto via Ollama, valida XSD + diff canônico (com gabarito) ou XSD + regra de
  negócio (sem gabarito), corrige em loop (máx. 3 iterações com gabarito, 2 sem), cooldown de 4h
  por layout via `AiFallbackSuppressionGate`. Confirmado: `Program.cs:441`, `AiTransformationCandidateService.cs`
  linhas 1-602, testado em `tests/.../Ai/AiTransformationCandidateServiceTests.cs`.
- **Camada 0 determinística (DSL→JSON estruturado)**: `MappingStructureService` (Scoped,
  `Program.cs`), consumindo `DslStructuredParser` de `ai/XslSynth.Contracts`. Não é LLM — é o
  contexto estruturado que alimentaria um LLM, já em produção via
  `POST /api/transformationexecution/execute-candidates` (retorna `sectionMappings`/`fieldMappings`).

### Existe mas desconectado do runtime (precisa só de wiring, ou de decisão de escopo)
1. **`RepairOrchestrator` / `OllamaXslSynthesizer` (síntese de XSLT real)** — `ai/XslSynth.Core/Core/RepairOrchestrator.cs`.
   Isso é o componente que corresponde literalmente à promessa do README §5 ("o back-end gera
   sozinho o XSLT"). Roda hoje só via `ai/XslSynth/Program.cs` (CLI, Linux/WSL, fora do
   `.csproj` da API via `DefaultItemExcludes`). **Esforço: grande.** Não é só registrar no DI —
   precisa (a) decidir se substitui ou complementa o pathway atual de XML-direto,
   (b) resolver a dependência Linux/WSL-only vs runtime Windows-only da API (crypto Sysmiddle),
   (c) expor como endpoint ou hook assíncrono equivalente ao `execute-candidates`.
   **Bloqueado por:** decisão do dono/arquitetura sobre se vale unificar os dois loops (XML-direto
   vs síntese de XSLT) ou mantê-los propositalmente separados — ver `xslsynth-trilha-a-overlap.md`,
   é o alvo ativo da Trilha A (Lia), não território livre.
2. **Migração para Saxon (XSLT 2.0/3.0)** — hoje `ai/XslSynth` usa `XslCompiledTransform` (XSLT 1.0),
   gap conhecido e documentado no próprio README do projeto, nunca endereçado. **Esforço: médio.**
   Só relevante se/quando o item 1 acima for priorizado.

### Não existe, precisa ser construído do zero
1. **Geração de TCL via IA.** Não há nenhum caminho — CLI ou runtime — que peça a um LLM para
   produzir um `.tcl`. `TclGeneratorService.cs`/`ImprovedTclGeneratorService.cs` são 100%
   determinísticos (regras de código, não prompt). Se a visão de produto inclui "IA gera TCL",
   isso é trabalho novo do zero: prompt design + parser de saída + validador (não existe
   "XSD do TCL" — precisaria de um verificador estrutural próprio, provavelmente reaproveitando
   `TclLineInfo`/`TclFieldInfo` já existentes em `TransformationLearningService.cs` como oráculo).
   **Esforço: grande.** **Bloqueado por:** decisão do dono se TCL realmente precisa de geração via
   IA — hoje o TCL é lido/gerado deterministicamente a partir da spec Excel (`ai/XslSynth/Excel/TclGenerator.cs`),
   então pode não haver gap de produto real aqui, só gap em relação ao texto do README.
2. **Unificação/atualização do README §5** — o texto atual (loop 5 passos: INDEX/RETRIEVE/GENERATE/VALIDATE/CORRECT
   sobre XSLT) descreve o desenho do `RepairOrchestrator`/CLI, não o que roda em produção
   (`AiTransformationCandidateService`, que não faz RAG/retrieval de exemplos similares — o prompt
   é fixo, sem vector store). Não há vector store/embeddings em lugar nenhum do código
   (`RAGService` citado no README não foi localizado nesta investigação — confirmar se existe ou
   é aspiracional). **Esforço: pequeno** (é documentação, não código) — mas é pré-requisito pra
   `@lp-parser-llm` não trabalhar contra uma spec desatualizada. **Bloqueado por:** nada, delegar a
   `@lp-doc` depois que o dono confirmar qual dos dois loops é a direção real.

## Itens críticos, em ordem de prioridade (para o dono/despacho)

1. **Decidir a direção**: o produto quer (a) continuar com XML-direto via Ollama (simples, já
   funciona, sem XSLT reutilizável) ou (b) migrar pro `RepairOrchestrator`/XSLT real (mais fiel à
   visão declarada, mas exige integrar um subsistema Linux/WSL-only ao runtime Windows-only e não
   está testado em produção). Sem essa decisão, qualquer trabalho de `@lp-parser-llm` corre risco
   de ir na direção errada.
2. **Confirmar se `RAGService`/vector store existe de fato** — o README cita como um dos serviços
   "que já materializam essa visão", mas não foi encontrado nesta investigação fora de comentários.
   Se não existir, o "R" de RAG é aspiracional e o loop real é few-shot fixo, não retrieval.
3. **Fechar a lacuna TCL** — ou é falta de escopo real (a visão pretende IA gerar TCL) ou é
   desalinhamento de texto (TCL já é gerado deterministicamente e está OK assim). Decisão do dono,
   não descoberta técnica.
4. Só depois dos 3 itens acima: `@lp-parser-llm` decide se conecta o `RepairOrchestrator` ao
   runtime (grande) ou se apenas atualiza/expande o loop de XML-direto já em produção (pequeno-médio,
   ex.: adicionar retrieval real de exemplos similares).

## Nota metodológica

Não foi possível testar o Ollama ao vivo nesta sessão (sem acesso de rede ao host que roda o
Ollama a partir deste ambiente) — a análise é 100% baseada em leitura de código
(`AiTransformationCandidateService.cs`, `Program.cs`, `RepairOrchestrator.cs`) e do README atual,
não em execução real. `OllamaOptions:Url` é lido de config; não foi verificado se aponta para um
endpoint vivo neste momento.
