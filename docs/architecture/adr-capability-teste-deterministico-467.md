# ADR — Capability `DeterministicTest` por engine no Fiscal Test Lab

- **Data:** 2026-09-22
- **Status:** Proposto
- **Autor:** `@lp-architect` (Aria)
- **Issue de referência:** `LayoutParser/LayoutParserApi#467`
- **Relacionadas:** #421 (runner determinístico de TCL), #423 (suite versionada), #226/#227
  (Slice 4 — `MappingExplanation`/`EngineCapabilities`), #415 (capability query Sysmiddle,
  fechada), #232 (`MappingEngineGuardFilter`); front: `LayoutParserReact#199`.

## 1. Contexto

O React (#199) mostra hoje um aviso estático dizendo que TCL não pode ser testado de forma
determinística no Fiscal Test Lab. Isso ficou desatualizado desde a #421: `TclRuleApplier` +
`MappingTestRunService.RunTclTestAsync` executam de verdade, produzindo o mesmo contrato
`MappingTestRunSummary` que `RunXsltTestAsync` (`Models/Entities/Fiscal/MappingRelease.cs:74`).
Não existe hoje nenhum metadado que o front possa consultar para descobrir isso — só suposição.

## 2. Confirmação: por que `sysmiddle` continua NÃO determinístico

Lido `Services/Fiscal/MappingTestRunService.cs:125-126`: o dispatch de test-run só conhece dois
ramos — `release.Engine == "tcl" ? RunTclTestAsync : RunXsltTestAsync`. **`sysmiddle` nunca
entra no Fiscal Test Lab.** `SysmiddleExplanationAdapter` (`Services/Fiscal/
SysmiddleExplanationAdapter.cs:48-49`) já hard-coda `Execute: true, Explain: true, Author:
false, Compile: false, Publish: false` — o `Execute: true` ali é sobre o motor Sysmiddle em
produção (fora deste projeto) explicar o que faz, não sobre este projeto conseguir rodá-lo e
testá-lo. Confirmado em `docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md` §2
(em `develop`/`master`): o runner Sysmiddle in-process trava na inicialização de licença do
host FiatMQ — bloqueio que segue de pé desde 2026-07-12, sem mudança nesta sessão. Não há
runner determinístico para Sysmiddle porque não há runner nenhum; o engine só participa do
fluxo de explicação read-only (`GET .../explanation`), nunca de `createTestRun`.

## 3. Onde expor — decisão

**Campo aditivo em `EngineCapabilities` (opção a da issue), sem endpoint novo e sem mudar o
resultado de `createTestRun`.**

Motivo: `EngineCapabilities` já é o lugar canônico onde o front consulta "o que este engine
pode fazer" — é retornado em `GET .../explanation`, que o Mapping Studio (React#199) já chama
antes de decidir o que oferecer na UI (autoria, compilação, etc.). Adicionar um campo aqui é
**zero chamadas HTTP novas**: o front já tem a resposta em mãos no momento em que precisa
decidir se mostra o aviso. As outras opções perdem nesse critério:

- **(b) endpoint dedicado `GET .../capabilities`** — capability é estática (hard-coded por
  adapter, nunca lida de config, ver comentário em `MappingExplanation.cs:22`), então um
  endpoint próprio só adicionaria uma chamada redundante com o que `explanation` já devolve.
  Só se justificaria se o front precisasse da capability **sem** já estar buscando explicação
  — não é o caso aqui: o aviso vive na mesma tela que consome `explanation`.
- **(c) no resultado de `createTestRun`** — errado por natureza: capability é propriedade do
  **engine**, não do **resultado de uma execução**. Colocar lá obrigaria o front a já ter
  rodado um teste para saber se pode confiar no botão de rodar teste — inverte a ordem causal
  que o React quer resolver (decidir *antes* de chamar `createTestRun`). Não descarto, mas não
  é aditivo puro: seria reintroduzir a mesma informação em dois lugares, com risco de
  divergência entre o valor estático e o que a run realmente fez.

**Não introduzo endpoint novo nem campo em `createTestRun`.** `RequiredGatesPassed`/
`Divergences` em `MappingTestRunSummary` já contam o resultado real de uma execução — isso é
suficiente e não precisa ser duplicado pelo conceito de "o runner é determinístico".

## 4. Nome e semântica do campo

Campo: `DeterministicTest` (bool), acrescentado em `EngineCapabilities`:

```csharp
public sealed record EngineCapabilities(
    bool Execute,
    bool Explain,
    bool Author,
    bool Compile,
    bool Publish,
    bool DeterministicTest = true); // trailing com default — aditivo, não quebra call sites existentes
```

`DeterministicTest = true` significa: **o Fiscal Test Lab (`createTestRun`) consegue executar
este engine de ponta a ponta e a mesma entrada sempre produz o mesmo resultado** — não
"validado contra a execução real do motor original em produção". É uma propriedade do TEST
RUNNER local (`MappingTestRunService`), não do gabarito de geração. Isso é
**deliberadamente distinto** de `validationBasis: declared_dsl` usado no gerador de amostras —
aquele conceito descreve a origem do XML esperado (gabarito declarado vs. execução Sysmiddle
real); este descreve se o motor de teste consegue rodar/repetir a comparação por conta própria.
Um mapper `sysmiddle` pode ter um gabarito `declared_dsl` de alta confiança e ainda assim ter
`DeterministicTest: false`, porque não há runner aqui que o execute.

Valores por engine, hard-coded em cada adapter (mesmo padrão de `FixedCapabilities` já usado):

| Engine | `DeterministicTest` | Motivo |
|---|---|---|
| `tcl` | `true` | `TclRuleApplier` interpreta as regras estruturadas via XPath, sem motor Tcl externo — determinístico por construção (#421). |
| `xslt` | `true` | `XslCompiledTransform` real, execução local determinística. |
| `sysmiddle` | `false` | Nunca entra em `createTestRun` (§2); runner bloqueado por licença FiatMQ; o adapter só explica. |

## 5. Contrato de resposta (exemplo)

`GET /api/mapping-explanations/{mappingId}?engine=tcl&version=draft` (recorte relevante):

```json
{
  "mappingId": "2f6c9e9a-...",
  "version": "draft",
  "engine": "tcl",
  "capabilities": {
    "execute": false,
    "explain": true,
    "author": true,
    "compile": false,
    "publish": false,
    "deterministicTest": true
  }
}
```

Para `engine=sysmiddle`:

```json
{
  "capabilities": {
    "execute": true,
    "explain": true,
    "author": false,
    "compile": false,
    "publish": false,
    "deterministicTest": false
  }
}
```

### XML doc do campo novo

```csharp
/// <summary>Capacidades do motor por trás de uma explicação (Slice 4, design §1). Nunca lidas de config — cada adapter hard-coda as suas.</summary>
/// <param name="DeterministicTest">
/// <c>true</c> quando o Fiscal Test Lab (<see cref="IMappingTestRunService"/>) consegue executar
/// este engine de ponta a ponta e a mesma entrada sempre produz o mesmo resultado — propriedade
/// do TEST RUNNER local, não do gabarito de geração (não confundir com <c>validationBasis:
/// declared_dsl</c> do gerador de amostras, que descreve a origem do XML esperado, não se o
/// motor de teste consegue rodá-lo). <c>tcl</c>/<c>xslt</c> = <c>true</c> (#421);
/// <c>sysmiddle</c> = <c>false</c> — nunca participa de <c>createTestRun</c>, só explica
/// (read-only), e o runner real segue bloqueado por licença FiatMQ (ver
/// <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c> §2). Default
/// <c>true</c> só existe para não quebrar call sites existentes ao tornar o parâmetro aditivo —
/// todo adapter real deve declarar o valor explicitamente.
/// </param>
public sealed record EngineCapabilities(bool Execute, bool Explain, bool Author, bool Compile, bool Publish, bool DeterministicTest = true);
```

## 6. Consequências

- **Zero chamadas HTTP novas** para o front descobrir a capability — resolve o gap da #467 no
  mesmo round-trip que o Mapping Studio já faz.
- **Aditivo:** `TclExplanationAdapter`/`SysmiddleExplanationAdapter` precisam só ajustar seus
  `FixedCapabilities` estáticos; nenhum contrato existente quebra (parâmetro trailing com
  default).
- **Não resolve sozinho** o critério de aceite "React remove o aviso estático" — isso depende
  do front trocar a heurística hard-coded por leitura de `capabilities.deterministicTest`
  (LayoutParserReact#199, fora deste repo).
- **Risco de divergência monitorado:** se um dia `sysmiddle` ganhar um runner real (destrava o
  bloqueio FiatMQ), o dispatch em `MappingTestRunService` precisa ganhar um terceiro ramo ANTES
  de `DeterministicTest` virar `true` para esse engine — não é automático, é decisão humana
  consciente na hora.

## 7. Implementação (próxima rodada — `@lp-backend-dev`)

1. Acrescentar `DeterministicTest` em `EngineCapabilities` (`Models/Dtos/Fiscal/
   MappingExplanation.cs`), com XML doc acima.
2. Atualizar os 2 `FixedCapabilities` existentes: `TclExplanationAdapter` (`true`),
   `SysmiddleExplanationAdapter` (`false`).
3. Se/quando existir um `XsltExplanationAdapter` equivalente (confirmar se já existe — não foi
   lido nesta sessão), declarar `true` lá também.
4. Teste cobrindo os 3 engines (critério de aceite da issue) — um teste por adapter verificando
   o valor do campo na resposta de `ExplainAsync`.
5. `@lp-doc`: Swagger/XML docs (já cobertos pelo doc do record) + atualizar o texto do README
   que descreve `EngineCapabilities`, se existir.
6. Fora deste repo: LayoutParserReact#199 troca o aviso estático por leitura do campo.
