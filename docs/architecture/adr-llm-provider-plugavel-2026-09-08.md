# ADR — Provedor de LLM plugável (Ollama + nuvem opcional, gated por sensibilidade do dado)

Autor: `@lp-architect` (Aria). Pedido do dono, 2026-09-08: tornar o provedor de LLM
configurável (hoje só Ollama local) para permitir também Anthropic, OpenAI, Kimi2 ou outros
provedores de nuvem, mantendo fine-tuning local como opção paralela — **restrição
não-negociável confirmada pelo dono**: nuvem só com dado seguro (sintético/anonimizado/teste),
nunca com documento fiscal real de cliente.

---

## 1. Onde o dado sensível entra hoje — mapeamento por call-site

Levantamento direto do código (não suposição). Existem hoje **3 call-sites de Ollama na API**
+ **1 no subprojeto `ai/XslSynth.Core`** (usado tanto em produção quanto em batch offline), com
3 `Options`/clientes HTTP diferentes — não há abstração de provider hoje, é acoplamento direto
a `OllamaOptions`/`HttpClient` em cada serviço.

| Call-site | Arquivo | Dado que trafega | Classificação |
|---|---|---|---|
| Diagnóstico de erro de validação (XSD/parsing) | `Services/XmlAnalysis/OllamaValidationDiagnosticService.cs` | Mensagem de erro + trecho do XML transformado **do documento fiscal real** que falhou validação | 🔴 **REAL — nunca pode ir para nuvem** |
| Sugestão de `MappingDraftRule` (Slice 3, issue #230) | `Services/Fiscal/MappingSuggestionService.cs` | Artefatos do draft (spec/XSD/amostra) — comentário no próprio arquivo (`ArtifactFileRef artifacts`) não deixa explícito se a amostra é sempre sintética ou pode ser um documento real anexado pelo analista ao criar o draft | 🟡 **AMBÍGUO — precisa confirmação, tratar como REAL até provar o contrário** (ver 1.1) |
| Síntese/reparo de XSLT (`RepairOrchestrator`, produção) | `ai/XslSynth.Core/Synthesis/OllamaXslSynthesizer.cs` via `RepairOrchestratorXslSynthesizerService.cs` | `input` = XDocument do parse posicional real do documento do cliente; `groundTruthXml` = saída real do Sysmiddle | 🔴 **REAL — nunca pode ir para nuvem** |
| Síntese/reparo de XSLT (mesmo motor, batch offline `MetricsBatchRunner`) | `ai/XslSynth.Core/Synthesis/OllamaClient.cs` | Mesmo par `(input, groundTruthXml)`, mas de um corpus de pares gabarito já persistido em disco para medição de modelo | 🔴 **REAL** (é o mesmo dado do dataset, só que reprocessado offline — não fica mais seguro por ser batch) |
| Geração de dado sintético (Slice de fixture) | `Services/Generation/Implementations/SyntheticDataGeneratorService.cs` | Existe um flag documentado no código que liga/desliga geração semântica via Ollama (comentário cita o Gemini decomissionado como "caminho anterior"); **por definição de propósito do serviço, o dado gerado é sintético — mas o INPUT do prompt (contexto/exemplo que guia a geração) pode incluir uma planilha/amostra real fornecida pelo analista como referência de formato** | 🟡 **AMBÍGUO no input do prompt, mas a SAÍDA é sintética por design** — ver 1.1 |

### 1.1 Honestidade sobre onde a separação NÃO é trivial

Dois dos cinco call-sites acima não têm separação limpa "real vs. sintético" hoje, e seria
enganoso apresentar isso como resolvido:

- **`MappingSuggestionService`**: o contrato do Slice 3 recebe `ArtifactFileRef` — arquivos
  anexados ao draft pelo analista (spec, XSD, "amostra"). Nada no código impede que a "amostra"
  seja um XML de produção real copiado por um analista para ilustrar o caso. Não há flag/campo
  que marque a proveniência do artefato como sintético vs. real.
- **`SyntheticDataGeneratorService`**: a saída é sintética por definição, mas o **prompt** que
  guia a geração pode embutir uma planilha real (`ExcelDataContext`, citado na memória de
  `@lp-architect` sobre geração sintética) como referência de estrutura/formato. Se esse
  contexto for enviado à nuvem, mesmo que a saída final seja rotulada "sintética", o **input**
  já vazou dado potencialmente real antes de qualquer geração acontecer.

**Consequência de design:** não dá para decidir "este fluxo pode usar nuvem" só pelo nome do
serviço. A decisão precisa ser **por chamada**, não por serviço — ver Seção 3.

---

## 2. Mecanismo técnico de bloqueio — não confiar em convenção documentada

Uma config global `Llm:Provider=Anthropic` que vale para toda a API é o desenho errado: ela
tornaria `OllamaValidationDiagnosticService` (dado real) capaz de rotear para nuvem só porque
alguém trocou uma flag pensando no `SyntheticDataGeneratorService`. O princípio de resiliência/
segurança deste projeto já rejeita esse tipo de acoplamento implícito (ver `security.md`:
"LLM em nuvem... nunca com documento fiscal real de cliente").

**Desenho proposto — dois mecanismos, um preventivo e um reativo:**

### 2.1 Preventivo — cada call-site declara sua própria sensibilidade, não uma flag global

Toda chamada ao provider plugável carrega um `DataSensitivity` explícito no contrato:

```csharp
public enum DataSensitivity
{
    RealFiscalDocument,   // documento/campo de cliente real — jamais nuvem
    SyntheticOrAnonymized // gerado, anonimizado, ou fixture de teste — nuvem permitida
}

public interface ILlmProvider
{
    string Name { get; }
    ProviderLocality Locality { get; }  // Local | Cloud
    Task<string> GenerateAsync(LlmRequest request, CancellationToken ct = default);
}

public sealed record LlmRequest(string Prompt, DataSensitivity Sensitivity, /* ... */);
```

`DataSensitivity` não é metadado decorativo — é **obrigatório no construtor do request**
(sem valor default que "esquece" de marcar), forçando quem escreve uma chamada nova a decidir
conscientemente.

### 2.2 Reativo — guarda em runtime no resolvedor de provider, não no chamador

O ponto de decisão fica num único lugar central (`LlmProviderResolver` ou nome equivalente),
não espalhado em cada serviço consumidor:

```csharp
public sealed class LlmProviderResolver
{
    public ILlmProvider Resolve(DataSensitivity sensitivity, string? requestedProviderName = null)
    {
        var provider = ResolveByName(requestedProviderName) ?? _defaultProvider;

        if (sensitivity == DataSensitivity.RealFiscalDocument
            && provider.Locality == ProviderLocality.Cloud)
        {
            // Nunca silencioso: loga como Error e recusa a chamada — não faz downgrade
            // automático pra Ollama sem o chamador saber, porque isso mascararia um bug
            // de configuração real (alguém configurou nuvem pra um fluxo que não devia).
            throw new InvalidOperationException(
                $"Recusado: provider de nuvem '{provider.Name}' não pode processar dado " +
                $"classificado como {sensitivity}. Configure um provider local para este fluxo.");
        }

        return provider;
    }
}
```

Isso é o "não oferecer nem a opção" do pedido do dono, só que expresso como recusa em runtime
em vez de ausência de UI — mais forte, porque cobre também configuração futura via
`appsettings`/env var feita por alguém que não leu a documentação. A recusa lança exceção (não
degrada silenciosamente): a única saída seria a app cair para "sem diagnóstico de IA" no
diagnóstico de validação, que já é o comportamento de resiliência existente quando o Ollama
está fora do ar — reaproveita o mesmo caminho, não precisa de tratamento novo.

### 2.3 Onde a classificação `RealFiscalDocument` é fixada — não configurável por env var

Para os 3 call-sites hoje confirmados como REAL (`OllamaValidationDiagnosticService`,
`OllamaXslSynthesizer`/`RepairOrchestrator`, `OllamaClient` do batch), o `DataSensitivity` é
**hardcoded no código-fonte do serviço**, não lido de config. Isso é deliberado: se fosse
configurável, um erro de config (`appsettings.json` errado num deploy) poderia reclassificar
dado real como sintético silenciosamente. A única forma de mudar a classificação de um
call-site é um PR revisado por humano alterando o código-fonte — fricção proposital.

Para os 2 call-sites ambíguos (Seção 1.1), a recomendação é: **classificar como
`RealFiscalDocument` até que exista um campo de proveniência explícito no artefato/contexto**
(ver Seção 6, item de escopo futuro) que permita ao próprio analista/sistema atestar
"isto é sintético". Até lá, nenhum dos dois é elegível para nuvem — mais restritivo do que o
necessário no caso comum, mas correto pelo princípio de segurança do projeto (não presumir
seguro sem prova).

---

## 3. Abstração de provider — `ILlmProvider`

### 3.1 Contrato mínimo

```csharp
namespace LayoutParserApi.Services.Llm;

public enum ProviderLocality { Local, Cloud }

public sealed record LlmRequest(
    string Prompt,
    DataSensitivity Sensitivity,
    string? JsonSchema = null,      // formato estruturado (já usado por OllamaValidationDiagnosticService)
    double Temperature = 0.0);

public sealed record LlmResponse(
    string Text,
    bool Success,
    bool TimedOut = false,
    string? ErrorMessage = null);

public interface ILlmProvider
{
    string Name { get; }              // "ollama-local", "anthropic", "openai", "kimi2"
    ProviderLocality Locality { get; }
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default);
    Task<bool> IsReachableAsync(CancellationToken ct = default);
}
```

Isso consolida em uma interface o que hoje está duplicado em 3 formas ligeiramente diferentes
(`OllamaValidationDiagnosticService` monta payload com `format` JSON Schema inline;
`OllamaClient` do XslSynth devolve métricas de tokens/s; `MappingSuggestionService` usa
`HttpClient` cru). **Não recomendo unificar tudo em um PR só** — ver Fases (Seção 7).

### 3.2 Reaproveitar `IXslSynthesizer` como precedente de forma, não substituir

`ai/XslSynth.Core/Synthesis/IXslSynthesizer.cs` já é a prova de que este projeto sabe fazer
essa abstração bem: interface enxuta (`SynthesizeRulesAsync`/`RepairFromDiffAsync`), duas
implementações (`OllamaXslSynthesizer` real, `MockXslSynthesizer` determinístico para demo).
`ILlmProvider` deve seguir o mesmo espírito — genérico o bastante para múltiplos providers, sem
vazar detalhe de payload HTTP de nenhum um deles no contrato. Não proponho fazer
`IXslSynthesizer` implementar/depender de `ILlmProvider` neste ADR (acoplaria dois subprojetos
com ciclos de vida diferentes — `ai/XslSynth.Core` é standalone, ver
`.claude/agent-memory/lp-architect/xslsynth-trilha-a-overlap.md`); a composição correta é
`OllamaXslSynthesizer` **usar** um `ILlmProvider` internamente como implementação de detalhe,
se e quando esse subprojeto quiser trocar de provider — decisão de `@lp-parser-llm`, não deste
ADR.

### 3.3 Reaproveitar a forma dos providers Gemini/OpenAI removidos, não o código

Os arquivos de `GeminiAIService`/equivalente OpenAI foram removidos no decommission de
2026-07-21 (ver `[[gemini-cloud-xsd-diagnosis-gap]]`/`[[gemini-openai-decommission-decision]]`
na memória de `@lp-architect`). **Não recomendo restaurá-los do histórico do git como ponto de
partida** — além de estarem desatualizados (SDKs de LLM mudam rápido), a razão de remoção não
foi "provider ruim", foi "dado sensível não pode ir pra nuvem sem controle" — o problema que
resolvemos agora era estrutural, não vai desaparecer só por copiar o código velho de volta. A
forma vale como referência (como cada um montava request HTTP, tratava erro, mapeava resposta),
mas cada provider novo (`AnthropicLlmProvider`, `OpenAiLlmProvider`, `Kimi2LlmProvider`) deve
ser implementado do zero contra o contrato `ILlmProvider` atual, com o gate de `DataSensitivity`
descrito na Seção 2 desde o primeiro commit — não como retrofit depois.

### 3.4 `OllamaLlmProvider` — o provider default, não descontinuado

`OllamaOptions` continua existindo (é config específica de Ollama: `Url`, `Model`,
`DiagnosisTimeoutSeconds`). `OllamaLlmProvider : ILlmProvider` passa a envolver o que
`OllamaValidationDiagnosticService`/`MappingSuggestionService` já fazem hoje via `HttpClient`
cru — sem mudar comportamento observável, só introduzindo a interface por trás. O modelo
fine-tunado (`layoutparser-sysmiddle-dsl:1.5b`, PR #335) continua sendo o `Model` configurado
para este provider — nada nele muda; fine-tuning e "provider plugável" são ortogonais (Seção 5).

---

## 4. Onde ficam as credenciais

Sem novidade em relação ao padrão já estabelecido (`.claude/rules/security.md`,
`dotnet-standards.md`):

- Dev: `dotnet user-secrets set "Llm:Anthropic:ApiKey" "<key>"` (ou seção equivalente por
  provider — `Llm:OpenAi:ApiKey`, `Llm:Kimi2:ApiKey`).
- Produção: variável de ambiente `Llm__Anthropic__ApiKey` etc., no mesmo mecanismo que já injeta
  `Database__Password` no ambiente do serviço Windows hoje (ver `security.md` §"Segredos no CI
  de dev").
- **Nunca em `appsettings.json`** — mesma regra que já existe, sem exceção nova para os
  providers de nuvem. Se o step de CI `gitleaks` (já existente) não cobrir o padrão de chave de
  API da Anthropic/OpenAI/Kimi2 especificamente, isso é um ajuste pequeno de regex a pedir para
  `@lp-devops` quando o primeiro provider de nuvem for implementado — não bloqueia este ADR.

---

## 5. Fine-tuning continua paralelo, não substituído

Nada neste desenho descontinua `layoutparser-sysmiddle-dsl:1.5b` (PR #335) nem o plano LoRA/
QLoRA já registrado em `.claude/agent-memory/lp-architect/fine-tuning-nichado-ollama-2026-09-02.md`.
`OllamaLlmProvider` continua sendo o provider default de todos os call-sites REAL (Seção 1) —
o modelo fine-tunado roda **dentro** dele, é só o `Model` configurado. Provider de nuvem é uma
opção **adicional**, só disponível onde `DataSensitivity == SyntheticOrAnonymized`, nunca uma
substituição do caminho local.

---

## 6. Onde a nuvem SERIA elegível hoje — e o que falta antes de habilitar

Dos 5 call-sites mapeados na Seção 1, **nenhum está pronto para nuvem hoje sem trabalho
adicional**, porque nenhum dos dois candidatos naturais (`MappingSuggestionService`,
`SyntheticDataGeneratorService`) tem hoje um campo de proveniência que distinga
"artefato/contexto sintético" de "artefato/contexto real" (Seção 1.1). Antes de qualquer PR de
provider de nuvem, é necessário (fora do escopo deste ADR, mas é o bloqueador real):

1. `MappingSuggestionService`: adicionar campo de proveniência no artefato do draft (ex.:
   `ArtifactProvenance { Synthetic, RealCustomerSample }`), preenchido pelo analista ao anexar
   o arquivo — sem esse campo, tratar como REAL por padrão (Seção 2.3).
2. `SyntheticDataGeneratorService`: separar explicitamente "contexto de referência enviado ao
   prompt" de "planilha real do analista" — se a geração semântica precisa de uma amostra real
   como referência de formato, ela deve permanecer no provider local (`Locality.Local`)
   mesmo que a saída seja sintética, porque o **prompt** já carrega dado real.

Só depois desses dois ajustes um provider de nuvem tem onde ser plugado com segurança real, não
apenas nominal.

---

## 7. Fases propostas

| Fase | Escopo | Risco | Depende de |
|---|---|---|---|
| **F1 — Abstração `ILlmProvider`, só Ollama por trás** | Introduzir `ILlmProvider`/`LlmProviderResolver`/`DataSensitivity` em `Services/Llm/`; `OllamaLlmProvider` envolve o `HttpClient` já usado por `OllamaValidationDiagnosticService` e `MappingSuggestionService` (refactor, sem mudar comportamento observável); os 3 call-sites REAL passam a declarar `DataSensitivity.RealFiscalDocument` explicitamente. Puro refactor de baixo risco — sem nuvem ainda. | Baixo | Nada — todas as peças existem, é reorganização. |
| **F2 — Fechar o gap de proveniência (Seção 6)** | Campo de proveniência em `MappingSuggestionService`/artefatos do draft; separação de contexto real vs. sintético no prompt do `SyntheticDataGeneratorService`. Pré-requisito real para F3, não cosmético. | Médio (mexe em contrato de dados existente) | F1 (usa `DataSensitivity` como o tipo que o campo de proveniência acaba mapeando). |
| **F3 — Primeiro provider de nuvem, só nos fluxos comprovadamente sintéticos** | Implementar `AnthropicLlmProvider` (ou o provider que o dono priorizar) contra `ILlmProvider`; habilitar apenas nos call-sites que F2 comprovou serem `SyntheticOrAnonymized`; config de credencial via user-secrets/env var (Seção 4); `LlmProviderResolver` com o guard de runtime (Seção 2.2) ativo desde o primeiro commit. | Médio — primeira integração externa nova desde o decommission de 2026-07-21, exige teste de resiliência (provider de nuvem indisponível/rate-limited) igual ao já existente para Ollama. | F1 + F2. |
| **F4 — Providers adicionais (OpenAI, Kimi2, etc.)** | Repetir F3 para cada provider adicional — trabalho incremental, mesmo contrato. | Baixo, uma vez que F3 valide o padrão. | F3. |
| **Fora de escopo permanente** | Habilitar provider de nuvem em qualquer call-site classificado `RealFiscalDocument` (diagnóstico de validação, síntese/reparo de XSLT contra gabarito real). Só reabre se o dono decidir explicitamente no futuro — não é uma fase futura implícita deste roadmap. | — | — |

**F1 é o único trabalho recomendado imediatamente.** F2 é obrigatório antes de qualquer nuvem
real acontecer — não é opcional nem pode ser pulado só porque F3 "parece" pronto tecnicamente.

---

## 8. Recomendação de issue

Recomendo ao `@lp-pm` abrir **uma issue para F1** (abstração `ILlmProvider`, escopo pequeno e
isolado, `@lp-backend-dev`) e **uma segunda issue para F2** (gap de proveniência, toca contrato
de dados em dois serviços diferentes — `@lp-backend-dev` + `@lp-parser-llm` revisando a parte de
`MappingSuggestionService`). F3/F4 (provider de nuvem de fato) recomendo **não abrir issue
ainda** — só depois que o dono escolher qual provedor priorizar (Anthropic vs. OpenAI vs. Kimi2)
e confirmar que aceita o resultado de F2 como pré-requisito, não como "detalhe a resolver depois".

---

## 9. Resumo executável para `@lp-backend-dev`

- **F1:** criar `Services/Llm/ILlmProvider.cs`, `Services/Llm/DataSensitivity.cs`,
  `Services/Llm/LlmProviderResolver.cs`, `Services/Llm/OllamaLlmProvider.cs`. Migrar
  `OllamaValidationDiagnosticService` e `MappingSuggestionService` para consumir
  `ILlmProvider` via `LlmProviderResolver.Resolve(DataSensitivity.RealFiscalDocument)` em vez
  de `HttpClient`/`OllamaOptions` diretos. Registrar no DI em `Program.cs`, mesmo grupo onde
  `OllamaOptions` já é configurado hoje (linha ~598). **Não mexer** em
  `ai/XslSynth.Core/Synthesis/OllamaClient.cs`/`OllamaXslSynthesizer.cs` nesta fase — projeto
  standalone, fora do escopo de F1 (ver Seção 3.2).
- **Guard de runtime (Seção 2.2) é obrigatório desde o primeiro commit de F1**, mesmo sem
  nenhum provider de nuvem existir ainda — é o mecanismo que F3 vai depender, melhor validar
  cedo com um teste que tenta registrar um provider `Locality.Cloud` fake marcado
  `RealFiscalDocument` e confirma que `Resolve` lança.
- **Não implementar F3/F4** (providers de nuvem reais) sem F2 concluído e sem confirmação
  explícita do dono sobre qual provedor priorizar.

---
*ADR de `@lp-architect` — análise, sem implementação. Push/PR e criação de issue ficam com
`@lp-devops`/`@lp-pm`.*
