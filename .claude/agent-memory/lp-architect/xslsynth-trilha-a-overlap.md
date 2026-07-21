---
name: xslsynth-trilha-a-overlap
description: ai/XslSynth é projeto .NET standalone isolado (não referenciado pela API) e é o alvo ativo da Trilha A (Lia) — qualquer nova tarefa ali precisa sequenciar com A2-A5, não é território livre
metadata:
  type: project
---

`ai/XslSynth/` é um **projeto .NET 10 console standalone** (`XslSynth.csproj`, `OutputType=Exe`),
explicitamente excluído da compilação da API via `DefaultItemExcludes` no `.csproj` da API. Confirmado por
grep (corrigido — a primeira tentativa tinha um filtro de exclusão de path quebrado): **zero referências
externas** a `XslSynth.Synthesis`/`XslSynth.Core`/`OllamaXslSynthesizer`/`DslRuleTranslator` em qualquer
`.cs`/`.csproj` fora da própria pasta, incluindo `mcp/`. Isso é arquitetura deliberada, não código órfão:
documentado em `docs/architecture/ia-xslt-synthesis.md` §9 — roda 100% em Linux/WSL, sobre dados exportados,
sem acoplar ao runtime Windows-only (cripto Sysmiddle). O README de `ai/XslSynth` confirma: "Fase 0-2", MVP,
usa `XslCompiledTransform` (XSLT 1.0) e sinaliza que produção deveria migrar pra Saxon (XSLT 2.0/3.0) — gap
conhecido, não resolvido.

Esse mesmo diretório é o alvo ativo da **Trilha A** do
`docs/architecture/multi-session-execution-plan.md` (ver [[track-a2-a5-spec]] e [[track-a-reconciliation]]),
dono `@lp-parser-llm` (Lia), com histórico extenso de sessões e status por fase (A2 parcial, A3
quase completa, A4 bloqueada por dado externo, A5 satisfeita com ressalva). `DslRuleTranslator.cs`
especificamente não está na lista de arquivos tocados por A1-A5, mas é parte do mesmo subsistema
(`ai/XslSynth/Synthesis/`) e do mesmo loop (`RepairOrchestrator`).

**Why importa:** em 2026-07-21 o dono do projeto propôs, numa conversa aparentemente sem contexto da Trilha A,
adicionar captura de proveniência dentro de `DslRuleTranslator.TranslateAsync` como item independente e
imediato. Isso cairia no meio de um workstream ativo e cuidadosamente sequenciado sem coordenação — risco de
Lia (ou quem implementar) pisar em trabalho em andamento, ou duplicar decisões já tomadas sobre este mesmo
loop (o "verificador determinístico" já documentado em `ia-xslt-synthesis.md` §2/§8 tem princípio de design
muito próximo do que a proveniência proporia).

**How to apply:** antes de despachar qualquer tarefa nova em `ai/XslSynth/`, checar primeiro o estado atual
de A1-A5 (`multi-session-execution-plan.md` §8-9) e considerar registrar a tarefa nova como uma fase adicional
sequenciada (ex. "A6"), não como trabalho solto — mesmo padrão de [[track-a2-a5-spec]].

**Resolução 2026-07-21 (Aria, passo 0):** registrada como **A6** em `multi-session-execution-plan.md` §10
(adicionei a fase ao doc). Sem conflito de arquivo previsto com o A2 residual (A2 mexe em `Excel/
XslGenerator.cs`, A6 mexe em `Synthesis/DslRuleTranslator.cs`) — podem rodar em paralelo, mas mesma dona
(Lia), que decide a ordem real dentro da própria fila. A6 não depende de A3/A4/A5. Motivação de negócio
(fora da Trilha A): alimenta o novo loop de diagnóstico Ollama que substitui a chamada Gemini existente —
ver [[gemini-openai-decommission-decision]].

**Correção de escopo (2026-07-21, mesmo dia, rodada seguinte):** A6 como registrada acima estava incompleta.
Lendo `ai/XslSynth/Core/LinkMappingTranspiler.cs` (os ~237 campos via LinkMapping direto, a maioria) achei
que esse caminho **não emite sidecar nenhum** — embute um `XComment` (`target=... input=... xpath=...`)
como filho literal do elemento de saída, dentro do próprio XSLT, que **sobrevive à transformação** (não é
instrução `xsl:`). Isso é o anti-padrão exato que a decisão de proveniência original queria evitar. A6
corrigida cobre `LinkMappingTranspiler.cs` **e** `DslRuleTranslator.cs`, e precisa de um passo de
"publicação" (inexistente hoje) que remova esse comentário de debug antes de qualquer XSLT ir pra
`Mapper.XslContent`. Detalhe completo em [[ia-fiscal-diagnosis-vision]] §3.1 (novo doc,
`docs/architecture/ia-fiscal-diagnosis-vision.md`).
