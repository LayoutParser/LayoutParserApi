# Pathway IA no `execute-candidates` — desenho formal (Issue #40)

> Autoria: `@lp-architect` (Aria) · Implementação: `@lp-parser-llm` (Lia), com apoio de
> `@lp-backend-dev` (Dex) no fio do controller/DI · Status: desenho aprovado, pronto para
> implementação. Não contradiz decisões prévias — consolida e aterrissa
> [[gemini-openai-decommission-decision]], [[no-fine-tuning-ai-decision]],
> [[xslsynth-trilha-a-workstream]] e o contrato de `[[multi-candidato-e-diagnostico-ia-contrato]]`
> dentro do fluxo real de `execute-candidates`.

---

## 1. Contexto / problema

`AutoTransformationGeneratorService` está registrado no DI (`Program.cs:364`,
`AddScoped<AutoTransformationGeneratorService>()`) mas **nenhum controller o injeta nem o
chama** — é um gerador de TCL/XSL "clássico" (heurísticas + `ImprovedTclGeneratorService`/
`ImprovedXslGeneratorService`), não o loop RAG com Ollama. O loop RAG de verdade
(gerar → aplicar → diff canônico → validar XSD → corrigir) **existe e funciona**, mas só como
CLI offline em `ai/XslSynth` (`RepairOrchestrator`, `OllamaXslSynthesizer`), fora do processo
da API, rodando em Linux/WSL sobre dados exportados manualmente.

`POST /api/transformation-execution/execute-candidates` (`TransformationExecutionController`)
já produz candidatos de dois pathways em paralelo — **sysmiddle** (low-code, via
`LowCodeAutoTransformationService`) e **tcl-xsl** (canônico, via `TransformationPipelineService`)
— normalizados em `TransformationCandidate { CandidateId, Pathway, TransformedXml, Score?,
SegmentMappings?, Validation?, FailureReason? }`. Não existe um terceiro pathway `"ia"`. Esta
issue fecha esse gap: acoplar o loop RAG existente ao fluxo real do endpoint, sem duplicar o
que já está resolvido em `ai/XslSynth`.

---

## 2. Decisões já fechadas pelo dono do projeto (não reabrir)

### 2.1 Quando o pathway IA roda

A IA **não é fallback condicional** — ela **sempre trabalha** para produzir o TCL/XSL/XSLT
"perfeito" para o layout+mapeador em questão. O gabarito (ground truth) é **sempre** o
resultado do pathway **sysmiddle** — nunca um XML "esperado" fornecido externamente, nunca
o resultado do pathway tcl-xsl. A IA gera com base no **layout e mapeador já existentes na
Sysmiddle hoje**, respeitando o que as **Funções Roslyn** do Sysmiddle fazem — algumas dessas
customizações são portáveis para XSLT, outras não (é específico por layout/mapeador). Escopo
real de uso hoje: **exclusivamente Fiat**; a visão é expandir para outros projetos Sysmiddle
depois — o desenho abaixo não deve hardcodar Fiat, mas também não deve gastar esforço
generalizando prematuramente para clientes que não têm corpus ainda.

### 2.2 Ranking/score entre candidatos

**Não existe ranking entre os três candidatos** (`sysmiddle`, `tcl-xsl`, `ia`). O botão que
dispara "pensar a transformação" já está correto hoje e **não deve ser alterado** por esta
issue. `recommendedCandidateId` em `TransformationExecutionCandidatesResponse` continua sendo
calculado do jeito que já é (maior `Score`, senão o primeiro) — o candidato `ia` **não** entra
artificialmente favorecido nem penalizado nesse cálculo; ele só participa se algum dia carregar
`Score` como os outros, o que este desenho **não propõe** fazer agora.

O que **vai existir como próximo passo, fora do escopo desta issue** (mas o desenho abaixo deixa
o gancho pronto): uma **comparação** entre o output do TCL/XSL/XSLT (humano ou IA) e o output do
pathway sysmiddle — não para "vencer" um ranking, mas porque a IA está sempre convergindo para o
mesmo gabarito. Essa comparação é exatamente o **diff canônico + XSD** que `ai/XslSynth` já
implementa (`CanonicalDiffer`, `XsdValidator`) — ver §5.

### 2.3 Timeout/custo

**Sem restrição definida.** Ollama pode ser lento; isso é aceitável nesta fase. Este ponto é o
que mais influencia o desenho (§4): significa que o pathway IA **não pode competir pelo mesmo
orçamento síncrono** que hoje governa `sysmiddle`/`tcl-xsl` em `execute-candidates`
(`LowCodeCandidatesBudget`, hoje pensado para runners x86 que terminam em segundos, não para um
loop de LLM que pode levar minutos por regra DSL).

---

## 3. Onde o pathway IA se encaixa no fluxo `execute-candidates`

### 3.1 Por que não pode ser um terceiro `Task` síncrono igual aos outros dois

O endpoint hoje roda `sysmiddleTask` e `tclXslTask` em paralelo sob um `CancellationTokenSource`
com teto calculado por `LowCodeCandidatesBudget` (dezenas de segundos, dimensionado para
processos x86 do runner). O princípio de resiliência do projeto (`dotnet-standards.md`
§"Resiliência") já proíbe deixar uma dependência externa lenta (aqui, Ollama) travar a resposta
principal ao usuário. Colocar a IA como terceira `Task` dentro do mesmo `Task.WhenAll` teria dois
efeitos ruins:

- Ou o teto do conjunto sobe para acomodar o pior caso do Ollama (minutos), degradando a
  experiência dos dois pathways que hoje respondem rápido;
- Ou o teto continua curto e a IA **nunca** teria chance de terminar dentro da janela, virando
  candidato morto por design — description contradiz 2.1 ("a IA deve sempre trabalhar").

**Decisão de desenho:** o pathway IA é **assíncrono/desacoplado** do ciclo síncrono de
`execute-candidates`. Reaproveita o mesmo padrão de "ticket consultável" que
`ParseController`/`LowCodeTransformationStore` já usam para `sysmiddle`
(`GET /api/parse/transformations/{ticket}`, `{ticket}/candidates/{mapperGuid}`) — não é um
mecanismo novo, é extensão do que já existe.

### 3.2 Fluxo proposto

```
POST /api/transformation-execution/execute-candidates
  │
  ├─ (síncrono, como hoje) sysmiddleTask + tclXslTask via Task.WhenAll, sob o
  │   orçamento existente (LowCodeCandidatesBudget) → response 200 com candidates[]
  │   (Pathway: "sysmiddle" | "tcl-xsl") — SEM MUDANÇA de comportamento aqui.
  │
  └─ SE sysmiddleTask produziu ao menos 1 candidato bem-sucedido (ground truth disponível):
       dispara EnqueueAiCandidateJob(...) — fire-and-forget (Task.Run, try/catch interno,
       nunca lança para fora, nunca atrasa a resposta HTTP já em voo).
       Job roda o loop RAG (ver §5) e, ao convergir ou esgotar tentativas, grava o resultado
       associado ao mesmo ticket que os outros pathways já usam.

GET /api/transformation-execution/execute-candidates/{ticket}/ia-status   (endpoint novo)
  → { status: "running" | "converged" | "failed" | "not-applicable",
      candidate?: TransformationCandidate (Pathway: "ia"),
      diagnostics?: { iterations, remainingDiffs, xsdValid } }
```

Pontos-chave:

- **Ground truth só existe depois do sysmiddle rodar** — reforça 2.1 (o gabarito é sempre
  sysmiddle) e também resolve o problema de "quando" disparar a IA: só faz sentido depois que
  o pathway sysmiddle já produziu o XML de referência para esta amostra/layout específico.
  Se sysmiddle não é aplicável (`autoResult.Applicable == false`) ou todos os candidatos
  sysmiddle falharam, o job de IA nem é enfileirado — vira `status: "not-applicable"` se
  perguntado, e um warning idêntico ao que já existe para os outros pathways (`"Pathway ia não
  aplicável: sem candidato sysmiddle (gabarito) disponível para este layout"`).
- **O array `candidates[]` da resposta síncrona de `execute-candidates` continua só com
  `sysmiddle`/`tcl-xsl`, como hoje.** Não é regressão de contrato: o front consulta o status/
  resultado da IA de forma assíncrona, análogo ao padrão de ticket que já existe. Se o produto
  quiser que o front saiba "há um candidato IA a caminho" sem fazer polling manual, a resposta
  síncrona pode incluir só um `AiCandidateHint { Ticket, Status: "queued" }` (campo novo,
  aditivo, não quebra consumidores atuais) — decisão de UX que cabe a `@lp-parser-llm`/front,
  não é bloqueante para este desenho.
- O "botão de pensar a transformação" (2.2) **não muda**. Este fluxo é adicional/paralelo a ele,
  não o substitui.

---

## 4. Interface/contrato que `@lp-parser-llm` implementa

Novo serviço, registrado como `Scoped` no grupo *Transformation* de `Program.cs`:

```csharp
namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Pathway IA de execute-candidates: gera TCL/XSL/XSLT via loop RAG (gerar → aplicar →
    /// diff canônico → validar XSD → corrigir), usando SEMPRE o output do pathway sysmiddle
    /// como gabarito. Porta o loop de ai/XslSynth (RepairOrchestrator) para dentro do processo
    /// da API como serviço invocável, sem duplicar a lógica do CLI standalone.
    /// </summary>
    public interface IAiTransformationCandidateService
    {
        /// <summary>
        /// Dispara o job assíncrono. NUNCA lança para o chamador (fire-and-forget) — toda falha
        /// vira estado "failed" consultável por GetStatusAsync. Implementação decide internamente
        /// se enfileira em memória, em Redis, ou em outro mecanismo — contrato não prescreve.
        /// </summary>
        Task EnqueueAsync(
            string ticket,
            string layoutName,
            Guid layoutGuid,
            string mapperGuid,
            string inputContent,       // mesmo TXT/XML que os outros pathways receberam
            string groundTruthXml,     // TransformedXml do candidato sysmiddle vencedor
            CancellationToken cancellationToken);

        Task<AiCandidateStatus> GetStatusAsync(string ticket, CancellationToken cancellationToken);
    }

    public class AiCandidateStatus
    {
        public string Status { get; set; } = "not-found"; // running | converged | failed | not-applicable | not-found
        public TransformationCandidate? Candidate { get; set; }   // Pathway = "ia", preenchido só quando converged
        public AiCandidateDiagnostics? Diagnostics { get; set; }
    }

    public class AiCandidateDiagnostics
    {
        public int Iterations { get; set; }
        public int RemainingDiffs { get; set; }   // 0 quando convergiu
        public bool XsdValid { get; set; }
        public string? LastError { get; set; }    // preenchido só em "failed"
    }
}
```

### 4.1 O que reaproveitar de `ai/XslSynth` (não recomeçar)

| Peça de `ai/XslSynth` | Papel no serviço novo |
|---|---|
| `Core/MapperExtractor.cs` | MapperVo (já descriptografado pela API) → LinkMappings + Rules + XslContent |
| `Core/DeterministicXslTranspiler.cs` | Baseline determinístico (LinkMappings → XSLT), sem IA |
| `Synthesis/OllamaXslSynthesizer.cs` | Chamada ao Ollama local para traduzir Rules DSL → XSLT e corrigir por diff |
| `Core/XsltApplier.cs` | Aplica o XSLT candidato sobre o input |
| `Core/CanonicalDiffer.cs` | Diff node-a-node vs o gabarito (aqui: `groundTruthXml` = output sysmiddle) |
| `Core/XsdValidator.cs` | Validação XSD do resultado |
| `Core/RepairOrchestrator.cs` | O loop inteiro (gerar → aplicar → diff → corrigir, repete até convergir) |

**Restrição arquitetural que já era conhecida** (`ia-xslt-synthesis.md` §9,
[[xslsynth-trilha-a-overlap]]): `ai/XslSynth` é deliberadamente standalone e roda em Linux/WSL
sobre dados **exportados manualmente**, sem tocar a cripto Sysmiddle (Windows-only). O serviço
novo dentro da API **não pode simplesmente referenciar o projeto `ai/XslSynth`** como está — ele
roda no processo da API (Windows, tem acesso à descriptografia via `LayoutParserDecrypt.exe` já
resolvida por quem monta `groundTruthXml`/mapper). Duas opções, ambas devem ficar registradas
como decisão em aberto para `@lp-parser-llm` resolver na implementação (não é decisão de
arquitetura bloqueante — é detalhe de empacotamento):

- **(a) Extrair `Core/`+`Synthesis/` para uma classlib compartilhada** (`XslSynth.Core.csproj`,
  sem `OutputType=Exe`), referenciada tanto pelo CLI `ai/XslSynth` quanto pelo novo serviço da
  API. É .NET 10 puro (sem dependência de cripto/Windows) — só o *código de extração* de
  `MapperVo` já é reaproveitado hoje via `MapperVo`/`MapperRule`/`LinkMappingItem` (que já vivem
  do lado da API). Recomendado — evita duas cópias do `RepairOrchestrator` divergindo.
- **(b) Reimplementar um subconjunto fino dentro da API**, mantendo `ai/XslSynth` só como
  bancada de métricas offline (Job 1 do pipeline descrito em [[ai-metrics-job1-job2-gaps]]).
  Mais rápido no curto prazo, mas cria duas implementações do mesmo loop — risco de dessincronia
  que o próprio `ai-metrics-job1-job2-gaps` já mostrou ser doloroso (métricas que não batem com o
  que roda de verdade).

Recomendo (a). `@lp-parser-llm` decide o timing (pode ser um passo prévio pequeno antes do resto
da issue).

### 4.2 Persistência do job

`EnqueueAsync`/`GetStatusAsync` não prescrevem mecanismo de storage — mas dado que
`ai-metrics-job1-job2-gaps.md` já registrou como dor real **"o Job 1 não persiste candidato
algum"**, este novo serviço **deve** persistir minimamente (arquivo em disco associado ao
ticket, seguindo o padrão de `LowCodeTransformationStore`, ou uma tabela nova) — não repetir o
mesmo buraco. Ground truth + XSLT convergido + diagnostics viram, de graça, dataset rotulado
para RAG/few-shot futuro (reforça o princípio "RAG, não fine-tuning" — mais exemplos indexáveis,
sem re-treinar nada).

---

## 5. Onde entra o "diff canônico + XSD" e o gancho para comparação futura (§2.2)

O próprio loop de convergência (`CanonicalDiffer`/`XsdValidator`, já reaproveitado) É o mecanismo
de comparação que o dono do projeto descreveu como próximo passo — só que hoje ele compara
**dentro do loop IA** (IA vs sysmiddle, para a IA se corrigir). O próximo passo (fora de escopo
aqui) é expor esse mesmo diff **para fora**, comparando qualquer candidato tcl-xsl (humano ou IA)
contra o sysmiddle, fora do contexto de "estou tentando convergir" — mais como um selo de
qualidade. Não implementar agora; só não construir nada que amarre `CanonicalDiffer`/
`XsdValidator` exclusivamente ao caso de uso "loop de correção", para não precisar refatorar
quando esse próximo passo vier.

---

## 6. Riscos e trade-offs

| Risco/trade-off | Nota |
|---|---|
| **Latência do Ollama sem timeout** (2.3) | Aceito pelo dono do projeto. Mitigado por ser assíncrono/fire-and-forget — não bloqueia a resposta HTTP de `execute-candidates`. Mas um job "running" para sempre (Ollama travado, processo morto) precisa de um teto de sanidade mesmo sem SLA de produto — recomendo um timeout técnico bem folgado (ex.: 30-60 min) só para não vazar jobs "running" eternamente na store; isso é higiene operacional, não a mesma coisa que o timeout de produto que o dono explicitamente disse não querer agora. Sinalizar essa distinção ao implementar. |
| **Hardware do servidor de produção** ([[production-server-hardware]]) | `BRNDDAPPBLD01`, i7-4790 Haswell 2014, sem GPU. Loop de correção pode ser lento na prática (medir, não assumir) — reforça por que não pode ser síncrono. |
| **Escopo hoje = só Fiat** | Não hardcodar `"Fiat"` no serviço — a seleção de layout/mapeador já vem por parâmetro do endpoint existente. O risco real é generalizar demais agora (ex.: heurísticas específicas de outro cliente) sem corpus para validar; não fazer isso. |
| **Duas implementações do mesmo loop** (`ai/XslSynth` standalone vs serviço novo) | Ver §4.1 — mitigado recomendando extração para classlib compartilhada. Se não seguido, risco real de dessincronia (já documentado como dor em [[ai-metrics-job1-job2-gaps]]). |
| **Sem fine-tuning, só RAG** ([[no-fine-tuning-ai-decision]]) | Este desenho não contraria — o loop reaproveitado já é RAG+verificador, não treina nada. |
| **Dado fiscal sensível** | Ollama local, nunca nuvem — já é o padrão do `OllamaXslSynthesizer` reaproveitado (`security.md`). Nenhuma mudança de postura aqui. |
| **Contrato aditivo, não quebra front atual** | `candidates[]` de `execute-candidates` não ganha o pathway `"ia"` nele — quem depende do shape atual não quebra. Endpoint de status é novo. Se o produto decidir mais tarde incluir `"ia"` no array síncrono (ex.: polling do próprio front chamando `execute-candidates` de novo), isso é decisão de produto/API versioning separada, não deste desenho. |
| **`[Authorize(Roles = "admin")]`** | O pathway IA herda a mesma restrição de `execute-candidates` (dispara processo/objeto caro) — o endpoint novo de status deve ter a mesma política, por consistência com o padrão já usado (Issue #32). |

---

## 7. Próximos passos (dispatch)

1. `@lp-parser-llm` (Lia): decidir e executar §4.1 (extrair `Core/`+`Synthesis/` de `ai/XslSynth`
   para classlib compartilhada, opção (a) recomendada).
2. `@lp-parser-llm`: implementar `IAiTransformationCandidateService` (§4) + persistência do job
   por ticket (§4.2), registrar no DI (`Program.cs`, grupo Transformation).
3. `@lp-backend-dev` (Dex): no `TransformationExecutionController`, após `sysmiddleTask`
   completar com sucesso, disparar `EnqueueAsync` fire-and-forget (padrão `Task.Run` +
   try/catch interno, `dotnet-standards.md` §Background work); adicionar
   `GET /execute-candidates/{ticket}/ia-status`.
4. `@lp-qa` (Quinn): quality gate específico — nenhum job IA pode travar a resposta síncrona de
   `execute-candidates` mesmo sob falha total do Ollama (teste de resiliência, não só teste de
   caminho feliz).
5. Fora de escopo desta issue, registrar como próximo item: expor o diff canônico como
   "comparação" standalone (§5), fora do loop de correção.
