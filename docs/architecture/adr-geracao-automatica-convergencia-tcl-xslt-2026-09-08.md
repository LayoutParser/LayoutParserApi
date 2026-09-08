# ADR — Geração automática + convergência iterativa TCL/XSLT contra o gabarito Sysmiddle (Issue #151)

Autor: `@lp-architect` (Aria). Decisão do dono, 2026-09-07/08. Este ADR fecha o problema do
"corpus vazio" que bloqueava a #151 (`tbMappingDraft`/`tbMappingRelease` com 0 linhas — ver
comentário de 2026-09-07 na issue): se a IA sempre gera e sempre converge contra o gabarito
Sysmiddle, o corpus deixa de depender de uso manual da feature — ele nasce como efeito
colateral de todo parse.

## 1. Contexto — o que já existe hoje (lido no código, não suposto)

A investigação encontrou mais infraestrutura pronta do que o pedido do dono presumia.
Resumo do estado real, arquivo por arquivo:

| Peça do pedido do dono | Estado real |
|---|---|
| "Sysmiddle sempre vira o gabarito" | ✅ Já é assim. `ExecuteSysmiddleCandidatesAsync` roda sempre; seu resultado (`GroundTruthXml`) é o input de `TryEnqueueAiCandidate`. |
| "IA sempre inicia a criação do mapeamento, não é mais opcional" | ✅ Já é assim, **desde a Issue #40**. Comentário textual no código (`TransformationExecutionController.cs:640-643`): *"o dono do projeto fechou que a IA 'sempre trabalha' nessa condição, não é um fallback condicionado ao tcl-xsl"*. Dispara sempre que há gabarito Sysmiddle + `LayoutGuid` resolvível — fire-and-forget, nunca atrasa a resposta síncrona. |
| "Loop gerar → validar → corrigir contra o gabarito, campo a campo" | ✅ Já existe, e já é 1:1 estrito. `RepairOrchestrator.RunAsync` (`ai/XslSynth.Core/Core/RepairOrchestrator.cs`) roda transpilar→(LLM completa)→aplicar→**`CanonicalDiffer.Diff`**→(LLM conserta) até `diffs.Count == 0 && xsd.IsValid` ou esgotar `maxIterations`. `CanonicalDiffer` é comparação estrutural node-a-node (não string diff ingênuo) e reporta o XPath exato de cada divergência — já é o mecanismo do critério de aceite "1:1", não "aproximado". O exemplo do dono (`<nNF>1</nNF>` vs `<nNF>001</nNF>`) já seria pego por esse differ como divergência de texto no mesmo nó. |
| "Se já existe TCL/XSL/XSLT, reaproveitar e corrigir — não recriar do zero" | ❌ **Não existe.** Gap real, tratado na Seção 3. |
| "Cada novo documento do mesmo layout dispara nova rodada de correção contra o TCL/XSLT já existente" | ❌ **Não existe** — cada chamada a `EnqueueAsync` roda `RepairOrchestrator.RunAsync` do zero (`_transpiler.Transpile(mapper)` como baseline, sempre a partir das `Rules`/`LinkMappings` do mapper, nunca a partir de um XSLT persistido anteriormente). |
| "Convergência vira dataset de treino (#151)" | ❌ **Não existe.** O dataset de treino (`ai/XslSynth/training-data/*.jsonl`, 6044 exemplos) é produzido por um job **offline/CLI** (`MetricsBatchRunner`, cron na VM Ubuntu) — nenhuma convergência em produção (`RunLoopAsync`/`RepairOrchestrator` dentro da API) grava exemplo nenhum. |

**Conclusão da investigação:** o pedido do dono não é "construir do zero" — é fechar 3 gaps
específicos num sistema que já faz 3 de 5 partes do que ele descreveu. Isso muda o escopo do
ADR de "desenho novo" para "extensão pontual e aditiva".

### 1.1 Dois pontos de persistência de XSLT já existentes, ainda desconectados

`RepairOrchestratorXslSynthesizerService.TryPersistXslt` já escreve o XSLT convergido em
`{XslPath}/{mapperName}_{layoutName}.xsl` (mesma convenção que o pathway `tcl-xsl` já lê,
issue #55) — mas é **escrita cega, nunca lida de volta**. O próximo `RunAsync` para o mesmo
mapper/layout não sabe que esse arquivo existe; recomeça do transpilador determinístico. É
literalmente o gap #3 acima, num único método, com uma solução pequena (Seção 3.1).

## 2. Ponto de disparo — resposta à pergunta 1

**Não muda.** `TryEnqueueAiCandidate` já é o gatilho certo — já dispara sempre que há
gabarito, sem depender de ação do usuário. A pergunta do dono ("hoje é só sugestão pro
usuário revisar, ou já é 'sempre inicia'?") tem resposta objetiva no código: já é "sempre
inicia" há a Issue #40, e o resultado convergido já vira `AiCandidateStatus.Converged` com
`TransformedXml`/`GeneratedXslt` prontos — não é uma sugestão passiva, é candidato executável.

O único ajuste de fluxo necessário aqui é **passar a intenção "documento novo do mesmo
layout" adiante** — hoje `TryEnqueueAiCandidate` não distingue "primeiro documento deste
layout" de "enésimo documento deste layout"; ambos rodam o mesmo `RunLoopAsync` do zero.
Ver Seção 3 para o mecanismo de distinção.

## 3. Mecanismo de reaproveitamento + correção iterativa — resposta às perguntas 2 e 3

### 3.1 Comparação campo a campo — reaproveitar `CanonicalDiffer`, não inventar

Já responde ao critério "1:1, não aproximado" (Seção 1). Não recomendo trocar por outro
mecanismo — `CanonicalDiffer` já normaliza espaço/atributo/namespace (evita falso-positivo
por formatação irrelevante) e é estrito no valor de texto de cada nó (pega `1` vs `001`).
`XsdValidationService`/`XsdValidator` seguem como segundo verificador (estrutural contra o
schema SEFAZ), já plugados no mesmo loop. Nenhuma peça nova aqui.

### 3.2 Reaproveitar o XSLT existente como seed, não descartar

Mudança concreta em `RepairOrchestratorXslSynthesizerService.SynthesizeAsync`: antes de
chamar `_orchestrator.RunAsync` com o baseline do transpilador determinístico, checar se já
existe `{XslPath}/{mapperName}_{layoutName}.xsl` de uma convergência anterior. Se existir,
usá-lo como ponto de partida do loop **em vez** do output do `DeterministicXslTranspiler` —
não elimina o transpilador (ele continua sendo a autoridade de baseline para mappers ainda
sem XSLT convergido), só passa a preferir o artefato já corrigido quando ele existe.

Contrato proposto (aditivo, sem quebrar a assinatura pública hoje usada por
`AiTransformationCandidateService`):

```
RepairOrchestrator.RunAsync(mapper, input, expectedXml, xsdPath, synthesizer, log,
                             maxIterations, ct,
                             seedXslt: XDocument? = null)  // NOVO parâmetro opcional
```

Quando `seedXslt != null`, o passo "baseline determinístico" (`RepairOrchestrator.cs:42-48`)
é pulado e `xslt = seedXslt` — o resto do loop (Evaluate → diff → LLM conserta) roda igual,
já corrigindo a partir do que existia. Isso é literalmente "reaproveitar e corrigir
iterativamente", como o dono pediu.

Se `xsd`/`diffs` derem zero **na primeira avaliação** do seed (documento novo, mas o XSLT
existente já dá conta dele), o loop converge em 0 iterações extras — ótimo, é o caso comum
esperado quando o layout já está estável.

### 3.3 Quem gera a correção — reaproveitar `IXslSynthesizer.RepairFromDiffAsync`, não criar novo papel de IA

Resposta à pergunta 3 do dono: **não precisa de um papel novo para a IA.** O passo 6 do
`RepairOrchestrator` (`RepairFromDiffAsync`) já é exatamente "input: XSLT atual + diffs
canônicos encontrados; output: XSLT corrigido" — o contrato que o dono descreveu no item 4
do pedido já existe, só nunca foi exercitado com "XSLT atual" vindo de uma execução anterior
persistida (sempre vinha do baseline determinístico da mesma rodada). Com o seed de 3.2, o
mesmo método passa a ser chamado com um XSLT "herdado" em vez de "recém-transpilado" — é
transparente para `IXslSynthesizer`, que só vê `xslt.ToString()` + `diffs`.

**Não recomendo** reaproveitar `MappingSuggestionService` para este papel — aquele serviço
resolve um problema diferente (propor `MappingDraftRule` estruturado a partir de spec/XSD/
sample, sem gabarito Sysmiddle disponível, Slice 3-5). O caminho relevante aqui já é o
`RepairOrchestrator`, que é o único subsistema com acesso ao gabarito Sysmiddle real em
runtime.

### 3.4 Chave de "mesmo mapeamento" — usar o que já existe

`TryPersistXslt` já usa `{mapperName}_{layoutName}` como chave de arquivo — é a mesma chave
que o pathway `tcl-xsl` usa para achar o XSLT publicado (issue #55). Reaproveitar essa chave
para o lookup de seed em 3.2 mantém uma única fonte de verdade de "qual arquivo pertence a
qual mapeamento", em vez de introduzir uma segunda convenção de nomeação.

## 4. Conexão com o dataset da #151 — resposta à pergunta 4

Cada convergência bem-sucedida do `RepairOrchestrator` (seed ou baseline, tanto faz) já
carrega todos os dados de um exemplo de treino: `input` (XDocument do parse posicional),
`groundTruthXml` (Sysmiddle), `FinalXslt` (convergido, 1:1 contra o gabarito), `Iterations`.
Hoje isso morre em `XslSynthesisResult`, devolvido só para virar `AiCandidateStatus`.

**Proposta:** um hook de captura, análogo em espírito ao `TryPersistXslt` (best-effort,
nunca bloqueia nem falha o loop), que grava o par `(input, groundTruthXml, FinalXslt)` em
formato compatível com o dataset já usado pelo LoRA (`ai/XslSynth/training-data/*.jsonl`,
ver `train_lora.py`) quando `report.Converged == true`. Isso transforma o dataset de
"produzido só por batch offline mensal" em "cresce incrementalmente a cada parse real que
convergiu em produção" — é a peça que fecha o "corpus vazio" citado no comentário da #151
de 2026-09-07 (`tbMappingDraft`/`tbMappingRelease` vazias): a #151 mede reversibilidade de
mappings TCL/XSLT **publicados**, e este ADR é o que passa a **produzir** esses mappings de
forma automática e volumosa, alimentando também a #151 por transitividade.

Formato exato do registro (schema JSONL, campos e path do arquivo) fica para
`@lp-parser-llm` decidir no PR de implementação — este ADR fixa apenas o ponto de captura
(dentro de `RepairOrchestratorXslSynthesizerService`, logo após `report.Converged`) e o
princípio de reaproveitar o schema já consumido por `train_lora.py`, não inventar um novo.

## 5. Escopo e riscos

### 5.1 UX — já não muda tanto quanto parece

Como a IA **já** sempre dispara desde a Issue #40, este ADR não é a mudança de UX
"usuário cria → IA ajuda" para "IA sempre cria" — essa mudança **já aconteceu**. O que muda
de fato é que o resultado convergido passa a ser **reaproveitado e refinado** ao longo do
tempo em vez de recalculado do zero a cada parse, e passa a alimentar um dataset. Não há
gap de UX novo a fechar.

### 5.2 Carga do Ollama — não aumenta na maioria dos casos, pode diminuir

Contraintuitivo, mas correto: com o seed (3.2), documentos subsequentes do mesmo layout que
já convergiram antes tendem a bater diff=0 já na primeira avaliação do seed (sem chamada
nenhuma ao Ollama, porque o loop só chama `synthesizer` dentro do `while`). Hoje, sem seed,
todo documento paga o custo total de `RepairOrchestrator` do zero. A carga só sobe no
primeiro documento de cada layout novo — que já paga esse custo hoje, sem mudança.

### 5.3 Custo de correção iterativa — teto já existe, não precisa de novo

`maxIterations` (`AiTransformationCandidateOptions.MaxIterations`, default 3) já é o teto de
tentativas antes de reportar `Failed` com o diff residual nos diagnostics — nenhuma decisão
nova de "quantas rodadas antes de desistir" é necessária, já existe e já é configurável.
Intervenção humana já é possível hoje via `ia-status`/revisão manual do candidato.

### 5.4 Degradação graciosa — já é o padrão do serviço, seed não quebra isso

O seed (3.2) é estritamente aditivo: se o arquivo `.xsl` não existir, ou estiver corrompido/
não for XML válido, cai para o baseline determinístico atual (comportamento de hoje) — mesmo
padrão try/catch já usado em `TryPersistXslt`/`ResolveXsdPath` no mesmo arquivo. Falha ao
ler o seed nunca deveria impedir a síntese — só perde a otimização de reaproveitamento.

### 5.5 Risco real a monitorar: seed ruim vira armadilha

Se um XSLT persistido convergiu por acidente contra um gabarito de baixa qualidade (ex.:
Sysmiddle com bug conhecido, ou documento atípico), reaproveitá-lo como seed propaga esse
erro para todos os documentos seguintes do mesmo layout, em vez de cada execução ter chance
de "recomeçar limpo" a partir do transpilador determinístico. Mitigação recomendada: manter
o transpilador determinístico como fallback automático se o seed reaproveitado **piorar**
(diff maior que o baseline teria dado) — não é código novo, é um `Evaluate` extra com o
baseline determinístico só quando o seed falha, comparando os dois antes de escolher com
qual iniciar o `while`. Deixo como recomendação para `@lp-backend-dev` avaliar custo/benefício
no PR (é uma segunda chamada de `_transpiler.Transpile` + `Evaluate`, barata — não chama IA).

## 6. Fases propostas

| Fase | Escopo | Tamanho | Depende de |
|---|---|---|---|
| **F1 — Seed de reaproveitamento (3.2)** | `RepairOrchestrator.RunAsync` ganha parâmetro opcional `seedXslt`; `RepairOrchestratorXslSynthesizerService` lê `{XslPath}/{mapperName}_{layoutName}.xsl` antes de chamar `RunAsync`, com fallback gracioso se ausente/inválido. | Pequeno — 1 PR, aditivo, sem mudança de contrato público quebrando chamadores existentes. | Nada — todas as peças (`TryPersistXslt`, convenção de nome, `RepairOrchestrator`) já existem. |
| **F2 — Fallback anti-armadilha (5.5)** | Comparar seed vs. baseline determinístico antes de escolher ponto de partida, quando o seed não converge de cara. | Pequeno-médio. | F1. |
| **F3 — Captura incremental de dataset (Seção 4)** | Hook best-effort em `RepairOrchestratorXslSynthesizerService` grava exemplo JSONL a cada `report.Converged == true`, formato compatível com `train_lora.py`. | Pequeno — 1 PR, aditivo, isolado do loop principal (try/catch próprio). | Nada — independe de F1/F2, pode ser feita em paralelo. |
| **F4 — Retraining recorrente consumindo o dataset incremental** | Fora de escopo deste ADR — decisão já registrada em `.claude/agent-memory/lp-architect/fine-tuning-nichado-ollama-2026-09-02.md` (LoRA/QLoRA nichado); F3 só alimenta o dataset que aquele ADR já previu consumir. Cadência de retraining (quando/quanto dado novo dispara um retrain) é decisão de produto separada, não técnica. | — | F3 + volume real. |

**F1 e F3 são independentes e pequenas — podem entrar em paralelo, cada uma como 1 PR.** F2
é um refinamento de robustez sobre F1, não bloqueante para começar a colher benefício. F4
não é deste ADR.

## 7. Recomendação de issue

Recomendo ao `@lp-pm` abrir **uma issue nova** para F1+F2 (seed de reaproveitamento —
implementação concreta em `RepairOrchestrator`/`RepairOrchestratorXslSynthesizerService`,
referenciando este ADR) e **uma segunda issue** para F3 (captura incremental de dataset,
referenciando também a #151 como consumidora indireta). Não recomendo uma issue única — os
dois escopos têm donos de revisão potencialmente diferentes (`@lp-parser-llm` para F1/F2 no
motor de síntese; `@lp-parser-llm` também para F3, mas com escopo de dataset/schema JSONL
que pode ser revisado independentemente) e nenhum depende do outro para entregar valor.

## 8. Resumo executável para `@lp-backend-dev` / `@lp-parser-llm`

- **Não implementar do zero** o pathway "IA sempre inicia a criação" — já existe desde a
  Issue #40 (`TransformationExecutionController.TryEnqueueAiCandidate`).
- **F1:** adicionar `seedXslt: XDocument? = null` em `RepairOrchestrator.RunAsync`
  (`ai/XslSynth.Core/Core/RepairOrchestrator.cs:30`); pular o baseline do transpilador
  quando fornecido. Em `RepairOrchestratorXslSynthesizerService.SynthesizeAsync`
  (linha ~119-135), antes de chamar `_orchestrator.RunAsync`, tentar ler
  `{_xslBasePath}/{mapper.Name}_{layoutName}.xsl` (mesma convenção de `TryPersistXslt`,
  linha 207-227) e passar como `seedXslt` se existir e for XML válido; `try/catch` best-effort,
  sem seed em caso de falha (comportamento atual preservado).
- **F3:** hook best-effort logo após `report.Converged` (linha 137 de
  `RepairOrchestratorXslSynthesizerService.cs`), gravando `(input, groundTruthXml,
  FinalXslt)` em formato JSONL compatível com `ai/XslSynth/training-data/*.jsonl` — schema
  exato a decidir por `@lp-parser-llm`, reaproveitando o que `train_lora.py` já consome.
- **Não mexer** em `CanonicalDiffer`, `XsdValidationService`/`XsdValidator`, nem no gatilho
  de `TryEnqueueAiCandidate` — já satisfazem o critério 1:1 e o "sempre dispara" do dono.

---
*ADR de `@lp-architect` — análise, sem implementação. Push/PR e criação de issue ficam com
`@lp-devops`/`@lp-pm`.*
