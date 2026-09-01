# Design: integrar RepairOrchestrator (ai/XslSynth.Core) ao runtime da API

Data: 2026-08-21 · Autor: @lp-architect (Aria) · Para: @lp-parser-llm (Lia)

## 1. O boundary Linux não é real

`ai\**` está excluído do build da API via `DefaultItemExcludes` em `LayoutParserApi.csproj`
(linha 12-14), mas o comentário no próprio `.csproj` diz o motivo: **"não colidir no build
(Program.cs duplicado + tipos de outros pacotes)"** — organização de projeto, não portabilidade.

Confirmado lendo os dois `.csproj` do lado gerador:
- `XslSynth.Core.csproj`: "100% .NET puro — sem dependência de cripto Sysmiddle nem de Windows."
- `XslSynth.Contracts.csproj`: "Zero dependência de HTTP/Ollama/cripto Sysmiddle."

`RepairOrchestrator`, `OllamaXslSynthesizer` e `OllamaClient` (`ai/XslSynth.Core/Core/` e
`Synthesis/`) não chamam `xsltproc`/`libxml2` nem qualquer binário externo via shell — a geração
de XSLT roda em .NET puro (validação XSD via `System.Xml`, diff canônico próprio) e a chamada ao
Ollama é HTTP (`OllamaClient`), igual ao que a API já faz noutros lugares. O único I/O é
`File.ReadAllBytes`/paths, portável. **Não existe boundary técnico Linux/WSL** — é rótulo
herdado do CLI standalone (`ai/XslSynth/Program.cs`), que é só o *host* console, não o motor.

Isso invalida as Opções A (`wsl.exe`) e C (microsserviço remoto) como necessárias — ambas
adicionariam latência de rede/processo e uma nova dependência externa "que pode cair" sem
nenhum ganho técnico. O próprio design doc `design-xslsynth-runtime-e-reversibilidade-2026-08-16.md`
§1 já antecipou isso ao extrair `XslSynth.Contracts` justamente para ser referenciado por
`Services/` da API — o precedente de arquitetura já aponta pra dentro do processo.

## 2. Opção recomendada: **B — referenciar `XslSynth.Core` in-process, dentro da API**

Igual ao padrão já usado por `XslSynth.Contracts` (referenciado por `ai/XslSynth.Core` **e**
puxável por `Services/`), a API deve referenciar `ai/XslSynth.Core/XslSynth.Core.csproj`
diretamente via `<ProjectReference>` — assim como o CLI standalone já faz. Não é preciso portar
nada: o código já é .NET 10 puro compatível com o `TargetFramework` da API.

**Trade-offs vs. as descartadas:**

| | B (in-process, recomendada) | A (WSL) | C (microsserviço remoto) |
|---|---|---|---|
| Nova dependência externa | Nenhuma | WSL2 no host Windows Server 2022 (não confirmado habilitado em produção) | Nova VM/serviço HTTP interno a manter |
| Latência | Zero (in-process) | Overhead de processo cruzando boundary WSL | Round-trip de rede |
| Resiliência | Falha = exceção .NET capturável no próprio try/catch do fire-and-forget | Novo ponto de falha (processo `wsl.exe` pode travar/não existir) | Novo ponto de falha (serviço fora do ar) — mas o projeto já tolera isso pro Ollama |
| Esforço | Baixo — 1 `ProjectReference` + wiring de DI | Médio-alto — infra + wrapper de processo | Alto — novo deploy, novo serviço a operar |
| Justificativa técnica real | Nenhuma dependência Linux existe | Nenhuma (dependência não existe) | Nenhuma (dependência não existe) |

Não há razão técnica pra pagar o custo de A ou C quando a causa raiz do isolamento é só
organização de arquivos de build.

## 3. Plano de execução em fases (para @lp-parser-llm)

### Fase 0 — Ajuste de build (pré-requisito)
- `LayoutParserApi.csproj`: adicionar `<ProjectReference Include="ai\XslSynth.Core\XslSynth.Core.csproj" />`
  explícita — hoje o `.csproj` **já tem** essa referência na linha 63 (verificado nesta sessão:
  `<ProjectReference Include="ai\XslSynth.Core\XslSynth.Core.csproj" />` existe desde a extração
  do `CanonicalDiffer`). Confirmar que o `DefaultItemExcludes` (`ai\**`) não conflita com a
  `ProjectReference` explícita — teste com `dotnet build` limpo; se colidir (glob duplicado do
  `Program.cs` do CLI sendo incluído via referência transitiva), isolar só os arquivos de
  `Program.cs`/CLI-only do XslSynth.Core em `<Compile Remove>` no `.csproj` dele, não na API.

### Fase 1 — Novo serviço de domínio na API
- Criar `Services/Transformation/Ai/RepairOrchestratorXslSynthesizerService.cs` (nome sugestivo,
  ajustar ao padrão do time), implementando uma nova interface `IXslSynthesizerService` com um
  único método assíncrono:
  ```csharp
  Task<XslSynthesisResult> SynthesizeAsync(
      MapperVo mapper,           // já resolvido via XslSynth.Contracts
      string inputContent,
      string? groundTruthXml,
      int maxIterations,
      CancellationToken ct);
  ```
  Internamente, instancia `RepairOrchestrator` + `OllamaXslSynthesizer` (via `OllamaClient`
  apontando pra config `Ollama:Url` já existente na API) e delega o loop gerar→validar→corrigir.
- `XslSynthesisResult` contém: `string GeneratedXslt`, `bool Converged`, `int IterationsUsed`,
  `IReadOnlyList<string> ValidationErrors` (mesmo vocabulário que `AiCandidateDiagnostics` já usa,
  pra não duplicar contrato).

### Fase 2 — DI em `Program.cs`
- Registrar no grupo **Transformation** (já existente), `Scoped`:
  ```csharp
  builder.Services.AddScoped<IXslSynthesizerService, RepairOrchestratorXslSynthesizerService>();
  ```
- `OllamaClient` já é HTTP — reaproveitar o `HttpClient` nomeado que a API já registra pro Ollama
  (checar se existe um `IHttpClientFactory` client "ollama" já configurado; se não, criar um,
  seguindo o padrão de resiliência do Redis opcional — timeout curto, sem exceção não capturada
  subindo até o request principal).

### Fase 3 — Plugar no ponto de disparo existente
- **Não é pathway adicional** — é a implementação real por trás de
  `IAiTransformationCandidateService.EnqueueAsync` (`Services/Transformation/Ai/
  AiTransformationCandidateService.cs`), que hoje gera XML direto via Ollama. Trocar o motor
  interno desse serviço para chamar `IXslSynthesizerService.SynthesizeAsync` em vez do caminho
  atual — o contrato externo (`ticket`, `GetStatusAsync`, `AiCandidateStatus`) não muda, só o
  que acontece dentro do loop.
- Isso preserva o disparo automático já existente em `execute-candidates` (sem gabarito) e no
  fallback de IA (`design-fallback-ia-automatico-2026-08-16.md`) — nenhum novo endpoint/trigger
  necessário.

### Fase 4 — Persistência do XSLT gerado (para o pathway `tcl-xsl` achar)
- Confirmar com `@lp-backend-dev` onde `TransformationPipelineService.cs` resolve `.tcl`/XSLT
  hoje (catálogo em disco vs. banco) e gravar o `GeneratedXslt` resultante no **mesmo local e
  convenção de nome** que os XSLT "reais" pré-existentes usam — provavelmente por
  `layoutGuid`/`mapperGuid`, mesmo padrão do catálogo GUID→XPath já usado pelo `XslSynth.Contracts`.
  Não inventar um segundo local de armazenamento — isso recria a duplicação de pathway já
  registrada em memória (`transformation-pathway-duplication.md`).

### Onde o TCL entra depois (não desenhar agora)
Quando XSLT/XML estiverem resolvidos, o mesmo `RepairOrchestrator`/loop de correção é o
candidato natural pra gerar TCL também — `ai/XslSynth.Core` já separa Synthesis de Model, então
adicionar um segundo `ISynthesizer` (TCL) reaproveitando o mesmo orquestrador de repair é o
encaixe esperado. Não requer redesenho do boundary — só um novo `IXslSynthesizerService`
overload ou implementação irmã.

## 4. Riscos explícitos da opção escolhida
- **Acoplamento de assembly**: a API passa a carregar `System.Reflection.MetadataLoadContext`
  (via `XslSynth.Contracts`) e todo o código de síntese em processo — aumenta a superfície de
  memória/GC do processo principal. Mitigação: é `Scoped`, não `Singleton`; e o loop já é
  fire-and-forget/background, não bloqueia request síncrono.
- **Falha do Ollama dentro do loop**: já coberto pelo padrão de resiliência existente
  (`AiCandidateStatus` vira "failed" consultável, nunca derruba o request — mesmo comportamento
  que `AiTransformationCandidateService` já tem hoje).
- **Build glob**: risco técnico real é só a Fase 0 (colisão de `DefaultItemExcludes` com
  `ProjectReference` explícita) — validar com `dotnet build` limpo antes de prosseguir.
