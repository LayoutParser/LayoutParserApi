# Plano técnico — 17 issues pendentes do backlog (2026-09-02)

> Desenho arquitetural das 17 issues genuinamente pendentes do repo `LayoutParser/LayoutParserApi`,
> produzido enquanto o fine-tuning nichado do Ollama roda em background (ver
> [`fine-tuning-nichado-ollama-2026-09-02`](../../.claude/agent-memory/lp-architect/fine-tuning-nichado-ollama-2026-09-02.md)).
> **Não é implementação** — é o desenho pronto pra `@lp-backend-dev`/`@lp-parser-llm` picarem
> quando o dono priorizar. Cada seção traz: problema real, abordagem recomendada, arquivos/serviços
> afetados, riscos/trade-offs, e o que falta decidir (se houver).

Convenção de profundidade: issues marcadas com 🔬 tiveram desenho aprofundado (maior valor técnico,
menor decisão pendente do dono). Issues marcadas com 🎯 são puramente decisão do dono — a seção só
lista opções concretas para escolha em uma frase.

---

## Índice

1. [#103 — autoria fiscal assistida (amostras+Excel+XSD)](#103) 🔬
2. [#97 — IA segregada por sessão de usuário (fase 2)](#97) 🔬
3. [#151 — reconstrução reversa best-effort XML→TXT (Fase 4)](#151) 🔬
4. [#173 — TransformationValidatorService sem validação detalhada](#173) 🔬
5. [#90 — capacidade registrada sem gate de DI](#90) 🔬
6. [#104 — teste e2e faltando para TryEnqueueAiCandidate](#104) 🔬
7. [#216 — expor detect_layout no MCP](#216) 🔬
8. [#196 — bug: colapso posicional LINHA006 (.mqseries)](#196)
9. [#221 / #219 / #218 / #137 — epic FIAT + auth m2m + mapeamento campo (cross-repo)](#221-219-218-137)
10. [#112 — MIGRATE_CONFIG_TO_REPO=true](#112) 🎯
11. [#110 — dry-run "config drift" contra produção](#110) 🎯
12. [#108 — drift appsettings.json prod vs repo](#108) 🎯
13. [#96 — FindXslFile: parâmetros mortos](#96) 🎯
14. [#88 — 27(28) achados SecurityCodeScan sem rastreamento](#88) 🎯

---

<a id="103"></a>
## 🔬 #103 — Autoria fiscal assistida (amostras + Excel + XSD)

### Problema real
Hoje a geração de mapeamento (TCL/XSLT) depende de um humano escrever a transformação do zero ou
de o loop de IA (RAG + Ollama, gerar→validar XSD→corrigir) convergir sozinho a partir de
input/output de exemplo. Para casos fiscais complexos (múltiplos CFOPs, lógica condicional
cross-seção, regras que dependem de tabela externa), o loop atual tende a estagnar porque XSLT é
fraco pra estado mutável/lógica de negócio complexa (achado já registrado em
`viabilidade-dlls-sysmiddle-para-rag.md` §5). #103 propõe um caminho **humano-no-loop**: o usuário
fornece amostras de entrada/saída + uma planilha Excel com a lógica de negócio (de/para de campos,
regras condicionais, tabelas de CFOP) + o XSD alvo, e o sistema usa isso como insumo estruturado
em vez de pedir ao LLM que infira tudo do zero a partir de XML bruto.

### Abordagem técnica recomendada — two-step, já validado em `session-artifacts-sharing-design`
Reaproveita a decisão já registrada em
[`session-artifacts-sharing-design`](../../.claude/agent-memory/lp-architect/session-artifacts-sharing-design.md):
**não pedir ao LLM "gere o XSLT" numa única chamada** partindo de exemplo cru. Dividir em dois
passos, cada um com um verificador determinístico próprio:

1. **Passo 1 — Extração estruturada da regra.** O Excel (de/para) é parseado para uma estrutura
   intermediária tipada (`FiscalMappingRule`: lista de `(campoOrigem, campoDestino, condição?,
   tabelaLookup?)`), sem LLM envolvido — é parsing determinístico de planilha (ClosedXML ou
   similar, já dentro do padrão .NET do projeto). O LLM entra só se a planilha tiver texto livre
   em células de "regra"/"observação" que precise virar condição estruturada — nesse caso, chamada
   Ollama **pequena e auditável**: "traduza esta célula de texto livre em uma condição estruturada
   {campo, operador, valor}", com o resultado **sempre revisável por humano antes de avançar**
   (não é fire-and-forget).
2. **Passo 2 — Geração de XSLT a partir da regra estruturada**, não do exemplo cru. Um gerador
   template-based (não-LLM) cobre os casos simples de de/para direto; casos com condição/lookup
   viram XSLT com `xsl:choose`/`xsl:key`. O LLM (Ollama) entra apenas para os fragmentos que o
   gerador template não cobre (transformação de string complexa, formatação condicional), sempre
   revalidado pelo loop existente (XSD + `CanonicalDiffer`).
3. As amostras de entrada/saída fornecidas pelo usuário viram os **casos de teste automáticos**
   do candidato gerado — reaproveita `AutomatedTransformationTestService`
   (`Services/Testing/AutomatedTransformationTestService.cs`) em vez de criar um comparador novo.

### Arquivos/serviços afetados
- **Novo:** `Services/Fiscal/FiscalMappingRuleExtractor.cs` (Passo 1 — parsing de Excel).
- **Novo:** `Services/Fiscal/TemplateXsltGenerator.cs` (Passo 2 — geração template-based a partir
  da regra estruturada).
- **Reaproveitado:** `Services/Testing/AutomatedTransformationTestService.cs` (validação contra
  amostras), `Services/Transformation/TransformationValidatorService.cs` (validação XSD/pipeline),
  `Services/Transformation/Ai/AiCandidateStore.cs` (armazenar candidato até promoção).
- **Novo endpoint:** `Controllers/FiscalMappingPackagesController.cs` já existe — provável extensão
  (`POST /api/fiscal-mapping-packages/from-excel`) em vez de controller novo, checar se o escopo do
  controller atual já cobre "pacote" no sentido certo antes de decidir.
- **DI:** registrar `FiscalMappingRuleExtractor`/`TemplateXsltGenerator` no grupo "Fiscal" já
  existente em `Program.cs` (mesmo grupo de `SysmiddleExplanationAdapter`/`TclExplanationAdapter`).

### Riscos / trade-offs
- **Parsing de Excel é superfície de ataque nova** (upload de arquivo binário complexo) — exige
  validação de tamanho/formato antes de abrir com ClosedXML, mesmo padrão de path-traversal já
  corrigido em outros uploads (ver commit `fix/path-traversal-pdf-orientations-172`).
  Confinar leitura de células a tipos primitivos (não fórmulas/macros — XLSX pode ter macro
  embarcada; usar biblioteca que não executa VBA).
- **Ambiguidade de célula texto-livre → condição estruturada** é o ponto mais frágil: se o usuário
  escrever regra em português natural complexo, o LLM pequeno (1-2B, CPU-only, ver
  `production-server-hardware`) pode falhar silenciosamente. Mitigação: revisão humana obrigatória
  antes de avançar pro Passo 2 (não fire-and-forget), e fallback explícito "não consegui
  estruturar esta célula, revise manualmente" em vez de adivinhar.
- **Escopo cresce fácil.** Recomendação: escopo mínimo viável é de/para direto + condição simples
  (`if campo X = valor Y`); lookup em tabela externa (ex.: tabela de CFOP completa) é fase 2 do
  próprio #103, não bloqueia o MVP.

### O que falta de decisão externa
- Formato exato esperado da planilha Excel (colunas fixas vs livre) — precisa de exemplo real do
  dono ou de um analista fiscal, não dá pra desenhar o parser sem um Excel de referência.
- Confirmar com o dono se `FiscalMappingPackagesController` é o lugar certo ou se nasce controller
  próprio (`FiscalAuthoringController`).

---

<a id="97"></a>
## 🔬 #97 — IA segregada por sessão de usuário (fase 2)

### Problema real
`AiCandidateStore` é `ConcurrentDictionary<string, StoredEntry>` global, chaveado só por `ticket`
(derivado de conteúdo+layout, sem entropia de usuário) — hoje inofensivo porque só contas `admin`
acessam os endpoints relevantes, mas abrir RBAC pra "qualquer usuário autenticado" (decisão já
registrada, ver `rbac-scope-xml-generic-2026-08-14`) sem isolar por usuário cria vazamento real
entre usuários. #97 fase 2 é sobre dar a cada usuário uma "sessão" de trabalho de IA com histórico,
não sobre implementar chat multi-turno.

### Abordagem técnica recomendada — reaproveita desenho já fechado
O desenho já está consolidado em
[`session-artifacts-sharing-design`](../../.claude/agent-memory/lp-architect/session-artifacts-sharing-design.md)
e no doc-mãe `docs/architecture/escopo-generico-txt-xml-e-acesso-por-papel-2026-08-14.md` §8 —
este documento apenas formaliza como plano de execução:

1. **Passo 1 (pré-requisito bloqueante, não paralelo):** particionar `AiCandidateStore` por
   `ICurrentUser.Name` — chave passa de `ticket` para `(userId, ticket)`. Mantém natureza de
   cache/TTL curto (rascunho efêmero está certo como está).
2. **Passo 2 — persistência de sessão de longo prazo.** Tabela SQL nova: `AiUserSession` +
   `AiUserSessionHistoryEntry` (guarda referência/status/timestamp, **não duplica** XSLT/TCL
   pesado — aponta para o artefato via ticket/id). Isso é o pedaço genuinamente novo: SQL como
   fonte da verdade para histórico persistente, Redis/`AiCandidateStore` continua sendo só
   working-set de curto prazo.
3. **Passo 3 — retomada pontual de ticket falho.** Endpoint que lê `AiUserSessionHistoryEntry` do
   usuário atual e permite reabrir um ticket específico (não uma "conversa" — é single-shot por
   ticket, sem memória de chamada Ollama entre tickets).
4. **Prompt customizado do usuário:** campo de sessão (persistido em `AiUserSession`), anexado
   *depois* do prompt de sistema fixo, nunca substituindo-o. Mitigação de risco de prompt injection
   fica no verificador determinístico existente (XSD + `CanonicalDiffer`), que não depende do LLM
   "se comportar" — não precisa de sanitização de prompt sofisticada além do básico.

### Arquivos/serviços afetados
- `Services/Transformation/Ai/AiCandidateStore.cs` — mudança de chave (Passo 1).
- **Novo:** migração EF/SQL para `AiUserSession`/`AiUserSessionHistoryEntry` (schema já mencionado
  em `feat/ai-user-session-schema-102` — **checar se #102 já criou o schema antes de recriar**;
  pelo log recente (`de8ec1a Merge pull request #270 from .../feat/ai-user-session-schema-102`)
  parece que a tabela já pode existir — issue #97 seria consumir/expor esse schema, não criá-lo).
- **Novo:** `Services/Transformation/Ai/AiUserSessionService.cs` (CRUD de sessão + histórico).
- Controllers afetados: qualquer endpoint que hoje lê `AiCandidateStore` diretamente por `ticket`
  (`execute-candidates`, `ia-status` — confirmar nomes exatos em `TransformationExecutionController`).

### Riscos / trade-offs
- **Ação #1 antes de qualquer coisa:** confirmar que a migração de #102 (`feat/ai-user-session-schema-102`,
  já mergeada) não deixa #97 parcialmente feita — evita retrabalho e permite que #97 comece do
  Passo 3 direto, não do zero.
- Particionar por `ICurrentUser.Name` pressupõe que a identidade do BFF é estável por usuário
  (confirmado — `TrustedIdentityMiddleware` já injeta identidade real). Risco baixo.
- Histórico de sessão sem limite de retenção vira acúmulo silencioso no SQL — definir TTL/rotina
  de limpeza (ex.: purgar sessões >90 dias sem acesso) desde o desenho, não como débito futuro.

### O que falta de decisão externa
- Confirmar com `@lp-backend-dev`/git log se `feat/ai-user-session-schema-102` já cobre o schema —
  isso muda o ponto de partida da implementação (schema pronto vs schema a criar).

---

<a id="151"></a>
## 🔬 #151 — Reconstrução reversa best-effort XML→TXT (Fase 4)

### Problema real
O pipeline hoje é unidirecional: TXT/MQSeries/IDOC → XML low-code → XML final (via TCL/XSLT).
#151 pede o caminho inverso: dado um XML (final ou intermediário), reconstruir um TXT posicional
"best-effort" — útil para depuração (comparar o TXT reconstruído com o original) e para casos onde
o consumidor só tem o XML e precisa recriar o formato legado.

### Abordagem técnica recomendada
"Best-effort" é a palavra-chave certa — não tentar simetria perfeita. Abordagem em camadas:

1. **Reutilizar o layout XML como fonte de posições.** O layout já descreve
   campo→posição/tamanho/tipo (é o mesmo artefato usado no parse direto). A reconstrução reversa
   é: para cada campo do layout, localizar o valor correspondente no XML (por XPath derivado do
   mesmo mapeamento campo↔XML usado no parse direto — não é um mapeamento novo, é o inverso do que
   `LayoutParserService`/`FieldMappingCompositionService` já fazem) e escrever na posição/tamanho
   declarados (padding conforme tipo: texto à esquerda, numérico à direita com zeros, conforme o
   layout já especifica para o parse direto).
2. **"Best-effort" declarado explicitamente no contrato de saída.** Resultado inclui não só o TXT
   reconstruído, mas uma lista de `ReconstructionWarning` (campo não encontrado no XML, campo
   truncado por exceder o tamanho da posição, campo com tipo incompatível) — o consumidor precisa
   saber que não é garantia de round-trip perfeito.
3. **Não reinventar o parser.** O serviço novo (`Services/XmlAnalysis/ReverseReconstructionService.cs`)
   depende do mesmo `LayoutDefinition`/`FieldMapping` que o parser direto usa — evita duas fontes de
   verdade sobre "onde fica cada campo".
4. **Escopo de layouts suportados:** começar só por TXT posicional fixo (o caso mais determinístico
   — largura de campo é fixa, então "onde escrever" nunca é ambíguo). MQSeries/IDOC ficam fora do
   MVP: são posicionais mas com nuances de linha variável (`WithBreakLines`, ver
   `idoc-textpositional-overload`) que tornam o "best-effort" mais arriscado de acertar sem casos
   reais pra validar.

### Arquivos/serviços afetados
- **Novo:** `Services/XmlAnalysis/ReverseReconstructionService.cs`.
- **Novo modelo:** `Models/Entities/ReconstructionResult.cs` (TXT reconstruído + `List<ReconstructionWarning>`).
- **Reaproveitado (leitura, não modificação):** `Services/Implementations/LayoutParserService .cs`
  (nome de arquivo já tem espaço no repo — cuidado ao referenciar), `Models/Entities/LineInfo.cs`.
- **Novo endpoint:** `Controllers/XmlAnalysisController.cs` — provável adição
  `POST /api/xml-analysis/reverse-to-txt` (já é o controller que trata análise de XML, mantém
  coesão em vez de controller novo).

### Riscos / trade-offs
- **Investigação, não feature pronta** — a issue está corretamente marcada como investigação: o
  primeiro passo real é rodar contra 3-5 layouts reais e medir taxa de campos não reconstruíveis
  antes de prometer qualquer SLA de fidelidade. Recomendo que a primeira entrega seja um **relatório
  de viabilidade** (script standalone, nem endpoint ainda) rodando contra amostras existentes de
  `ExpectedOutputs`, não direto pra produção.
- **Campos derivados/calculados no XML** (ex.: campo que no XML é resultado de concatenação XSLT de
  2 campos do TXT original) não têm caminho reverso determinístico — via de regra, viram
  `ReconstructionWarning`, não erro fatal, mas achatam a taxa de sucesso.
- Risco de escopo inflar para "IA reconstrói o que não dá pra mapear direto" — resistir; isso é
  fora do "best-effort" declarado na issue.

### O que falta de decisão externa
Nenhum bloqueio externo — é puramente técnico. Recomendo começar pela fase de medição antes de
comprometer prazo.

---

<a id="173"></a>
## 🔬 #173 — TransformationValidatorService sem validação detalhada

### Problema real
Lendo `Services/Transformation/TransformationValidatorService.cs`: o serviço já valida TCL (se
fornecido) e roda o pipeline completo (TXT→TCL→XSL→XML), mas a validação do resultado final contra
`expectedOutputXml` (quando fornecido) provavelmente é rasa — comparação estrutural/textual sem
detalhamento de *qual* campo divergiu, *por que* (valor diferente vs campo ausente vs tipo
incompatível), nem validação contra o XSD do documento fiscal alvo (NFe/CTe/NFCom/MDFe) de forma
explícita e reportada por campo.

### Abordagem técnica recomendada
1. **Reaproveitar `CanonicalDiffer`** (já citado em memórias como o comparador determinístico usado
   no loop de IA — confirmar path exato, provavelmente `Services/Transformation/Ai/`) em vez de
   criar um segundo comparador dentro de `TransformationValidatorService`. Hoje parecem ser dois
   caminhos de comparação paralelos (um pro loop de IA, outro pro validador manual) — unificar reduz
   superfície de bug e mantém "um único juiz determinístico" (princípio do projeto: loop
   gerar→validar→corrigir).
2. **Detalhamento por campo:** trocar o resultado binário (`Success: bool`) por uma lista de
   `FieldValidationDiff { XPath, Expected, Actual, DiffType }` (`ValueMismatch`, `MissingInOutput`,
   `UnexpectedInOutput`, `TypeMismatch`) — já existe `ValidationStep`/`Details` no modelo atual,
   é extensão, não reescrita.
3. **Validação XSD explícita e reportada:** se o documento é NFe/CTe/NFCom/MDFe (via
   `XmlDocumentTypeDetector`, já injetado no serviço), rodar contra o XSD correspondente
   (`XsdValidationService`, já existe no projeto conforme baseline de segurança recente) e anexar
   os erros de schema como `ValidationStep` próprio ("XSD Schema Validation"), não misturado com a
   comparação de conteúdo.

### Arquivos/serviços afetados
- `Services/Transformation/TransformationValidatorService.cs` (extensão do método
  `ValidateTransformationAsync` e provavelmente um novo método privado `CompareAgainstExpectedAsync`).
- `Services/Transformation/Models/TransformationValidationResult.cs` (ou equivalente) — adicionar
  `List<FieldValidationDiff>`.
- Reaproveitar `Services/Validation/XsdValidationService.cs` (confirmar nome exato) e o comparador
  determinístico do loop de IA (`CanonicalDiffer`).

### Riscos / trade-offs
- Baixo risco — é extensão aditiva de um serviço já testável, não reescrita de pipeline. Maior
  cuidado é não duplicar lógica de diff que já existe no loop de IA (revisar antes de escrever
  `CompareAgainstExpectedAsync` do zero).
- Formato de saída rico por campo pode ficar verboso para logs — usar `LogDebug` para o diff
  completo e `LogInformation` só para o resumo (contagem de diffs por tipo).

### O que falta de decisão externa
Nenhum. É tech-debt puro, pronto pra implementar.

---

<a id="90"></a>
## 🔬 #90 — Capacidade registrada sem gate de DI

### Problema real
Existem serviços de "capacidade" (`SysmiddleExplanationAdapter`, `TclExplanationAdapter`,
`XsltExplanationAdapter` em `Services/Fiscal/`) que presumivelmente se anunciam como disponíveis
(ex.: um registro de "capacidades" que o sistema expõe, tipo feature-flag ou catálogo de
adaptadores) mas não há verificação de que a dependência real por trás deles (ex.: binário
Sysmiddle, DLL de decrypt, runner) está de fato presente/configurada antes de declará-los
disponíveis. Resultado: o sistema promete uma capacidade no catálogo, e só falha em runtime, tarde
demais (mesmo padrão de falha silenciosa já visto em `lowcode-allowedpackageguids-empty-in-null`).

### Abordagem técnica recomendada
Introduzir um **gate de capacidade explícito no boot** (não em runtime, na primeira chamada):

1. **Interface `ICapabilityHealthCheck`** — cada adaptador de capacidade (`SysmiddleExplanationAdapter`
   etc.) implementa um método `Task<CapabilityStatus> CheckAvailabilityAsync()` que verifica sua
   dependência real (arquivo existe? runner responde? config obrigatória presente?).
2. **Registro condicional em `Program.cs`**, seguindo o padrão .NET já estabelecido no projeto para
   Redis opcional (`sp.GetService<T>()` nullable + log de Warning se ausente) — mas para capacidade,
   a diferença é: o serviço **é registrado sempre** (não é opcional como Redis), só que o *catálogo
   de capacidades disponíveis* (o que quer que consulte "quais adaptadores existem hoje") passa a
   consultar `CheckAvailabilityAsync()` em vez de assumir presença = disponibilidade.
3. **Endpoint de health check dedicado** (ou extensão do health check ASP.NET Core já padrão,
   `AddHealthChecks()`) que reporta por capacidade: `{ capability: "SysmiddleExplanation", status:
   "healthy" | "degraded" | "unavailable", reason: "..." }` — dá visibilidade operacional sem
   esperar o primeiro request real falhar.

### Arquivos/serviços afetados
- `Services/Fiscal/SysmiddleExplanationAdapter.cs`, `TclExplanationAdapter.cs`,
  `XsltExplanationAdapter.cs` — implementar a interface nova.
- `Program.cs` — registro do health check agregado no grupo de bootstrap.
- **Novo:** `Services/Interfaces/ICapabilityHealthCheck.cs`.

### Riscos / trade-offs
- Checar dependência externa no boot pode atrasar o startup se a checagem for síncrona/bloqueante
  sobre um recurso lento (ex.: chamar o runner LowCode pra "ver se responde") — usar timeout curto
  (2-3s) e nunca deixar o health check falhar o boot da API inteira (mesmo princípio de resiliência
  do projeto: degrade, não derrube).
- Não confundir "capacidade indisponível" com "erro" — o objetivo é visibilidade, não bloqueio; a
  API deve subir mesmo com capacidades degradadas, só reportando isso de forma clara.

### O que falta de decisão externa
Nenhum bloqueio — mas vale confirmar com o dono se o objetivo de #90 é só observabilidade (health
check) ou se ele quer que o sistema **recuse** requests para capacidades indisponíveis
(comportamento diferente: 503 explícito vs log de warning). Recomendo o primeiro (menos invasivo)
como MVP, com o segundo como extensão se o dono quiser.

---

<a id="104"></a>
## 🔬 #104 — Teste e2e faltando para TryEnqueueAiCandidate (double runner x86)

### Problema real
`TryEnqueueAiCandidate` (ou nome equivalente no fluxo de enfileiramento de candidato IA) depende de
um runner LowCode **x86** (processo externo `.exe`) — não dá pra testar e2e de verdade em CI sem
esse runner presente, e o runner real tem dependências de ambiente (registro, licença, caminho
fixo) difíceis de reproduzir em GitHub Actions. #104 pede avaliação de viabilidade de um **double**
(fake/stub) do runner para permitir teste e2e determinístico.

### Abordagem técnica recomendada — double de processo, não double de interface
Como o runner é um `.exe` externo chamado via `Process.Start` (não uma interface C# injetável
diretamente), o double certo não é um mock de classe — é um **executável fake** que imita o
contrato de I/O do runner real (mesmos argumentos de linha de comando, mesmo formato de
stdout/arquivo de saída, mesmo código de saída em sucesso/falha):

1. **Novo projeto de teste `tools/FakeLowCodeRunner/`** (console app x86 minimalista, .NET,
   plataforma `AnyCPU` ou `x86` para bater com o que o serviço real espera) que:
   - Lê o mesmo input que o runner real recebe (arquivo de config/mapper via argumento).
   - Produz saída determinística e configurável via variável de ambiente ou arquivo de fixture
     (`FAKE_RUNNER_SCENARIO=success|timeout|malformed_output|nonzero_exit`) — permite testar os
     4 caminhos de erro que a issue #104 provavelmente quer cobrir (sucesso, timeout do processo,
     saída mal formada, código de saída não-zero) sem precisar do binário real.
2. **Configuração via `RunnerPath` apontando pro double em ambiente de teste** — reaproveita o
   mesmo mecanismo de configuração já existente (`LowCode:RunnerPath` ou equivalente, já mapeado em
   `lowcode-runner-path-nunca-aponta-para-deploy-api`), só que apontando para o executável fake em
   vez do real. Isso é o que torna viável: **não precisa mudar o código de produção**, só a config
   do ambiente de teste e2e.
3. **Teste e2e** (`tests/LayoutParserApi.Tests/Transformation/TryEnqueueAiCandidateE2ETests.cs`)
   sobe a API com `RunnerPath` apontando pro double, dispara o enfileiramento real, e verifica o
   comportamento observável (candidato aparece no `AiCandidateStore`, status muda corretamente em
   cada cenário de fixture).

### Arquivos/serviços afetados
- **Novo projeto:** `tools/FakeLowCodeRunner/FakeLowCodeRunner.csproj`.
- **Novo teste:** `tests/LayoutParserApi.Tests/Transformation/TryEnqueueAiCandidateE2ETests.cs`.
- Configuração de teste: `appsettings.Testing.json` (ou equivalente) com `RunnerPath` apontando
  pro double compilado.
- CI: `.github/workflows/ci-dev.yml` (ou pipeline de testes) precisa buildar o double antes de
  rodar os testes e2e — **fora do alcance de `@lp-architect`/`@lp-backend-dev`, é `@lp-devops`**.

### Riscos / trade-offs
- **Fidelidade do double é o risco central**: se o contrato real do runner mudar (novo argumento,
  novo formato de saída) e o double não acompanhar, o teste e2e passa "verde" enquanto produção
  quebra — falso positivo pior que não ter teste. Mitigação: documentar o contrato assumido no
  próprio double (comentário no topo do `Program.cs` do fake) e revisitar sempre que o runner real
  mudar de versão.
- Exige plataforma x86 no ambiente de CI — verificar se o runner do GitHub Actions
  (self-hosted Windows, conforme `runner-dev-gh-actions`) suporta compilar/rodar x86 sem
  configuração adicional. Ponto a validar com `@lp-devops`/`@lp-qa` antes de comprometer o design.
- Escopo de "e2e" aqui é na verdade **e2e-do-processo-externo-simulado**, não e2e-de-verdade contra
  o binário Sysmiddle real — deixar isso explícito na descrição do teste para não criar falsa
  confiança de cobertura.

### O que falta de decisão externa
Avaliação de viabilidade de ambiente (x86 no runner de CI) cabe a `@lp-qa`/`@lp-devops`, não dá pra
fechar 100% do lado da arquitetura sozinha — mas o desenho acima já é suficiente para eles
avaliarem sem trabalho de design adicional.

---

<a id="216"></a>
## 🔬 #216 — Expor `detect_layout` no MCP

### Problema real
O MCP Server (`mcp/LayoutParserMcp/`) já expõe `parse_document` (`ParseTools.cs`), que detecta o
tipo de documento **internamente** como parte do fluxo de parse (`_layoutDetector.DetectType` em
`Controllers/ParseController.cs`), mas não existe hoje uma tool MCP nem endpoint HTTP dedicado que
exponha *só* a detecção de tipo/layout como operação isolada e reutilizável por um agente (ex.: um
agente que quer saber "que tipo de documento é este arquivo, e qual layout combina com ele" antes
de decidir o que fazer, sem já disparar o parse completo).

### Abordagem técnica recomendada
Seguindo o padrão do MCP já estabelecido (cliente fino sobre a API HTTP — ver `mcp-usage.md`), a
tool nova não implementa lógica nova, só expõe uma capacidade que precisa antes ser exposta como
endpoint HTTP na API:

1. **Novo endpoint na API:** `POST /api/parse/detect` em `Controllers/ParseController.cs`
   (mesmo controller, reaproveitando `_layoutDetector` já injetado) — recebe o arquivo (ou uma
   amostra, já que `DetectType` hoje opera sobre `sample` lido via `StreamReader`) e retorna:
   ```json
   {
     "detectedType": "txt" | "mqseries" | "idoc" | "xml",
     "confidence": "high" | "low",
     "suggestedLayouts": [ { "layoutName": "...", "score": 0.0 } ]
   }
   ```
   `suggestedLayouts` é o pedaço novo de verdade — hoje a detecção de *tipo* existe, mas "qual
   layout específico combina com este documento" (catálogo de layouts vs conteúdo) pode não
   existir como operação isolada; se não existir, um MVP razoável é reaproveitar o mecanismo de
   aprendizado/matching já usado internamente pelo parse (`LearningController`/serviço de
   catálogo) e só reportar o top-N por score, sem comprometer a com precisão alta ainda.
2. **Nova tool MCP:** `mcp/LayoutParserMcp/Tools/ParseTools.cs` ganha `DetectLayoutAsync`
   (`[McpServerTool(Name = "detect_layout")]`), seguindo exatamente o mesmo esqueleto de
   `ParseDocumentAsync` (correlationId via `CorrelationContext.NewId()`, `LogContext.PushProperty`,
   `IHttpClientFactory` cliente "api", tratamento de arquivo inexistente).
3. **Contrato de retorno da tool:** string JSON serializada (mesmo padrão de `parse_document`),
   documentada em PT/EN no atributo `[Description]`.

### Arquivos/serviços afetados
- `Controllers/ParseController.cs` — novo endpoint `detect` (extrai a lógica de detecção que hoje
  vive inline no método de upload para um método privado reutilizável, `DetectDocumentTypeAsync`,
  chamado tanto pelo upload quanto pelo novo endpoint — evita duplicar a lógica dos "casos
  especiais" já hardcoded: linha com 601 chars → mqseries, etc.).
- `mcp/LayoutParserMcp/Tools/ParseTools.cs` — nova tool.
- Se `suggestedLayouts` reaproveitar catálogo: `Services/Database/CachedMapperService.cs` ou
  serviço de catálogo equivalente (confirmar nome exato antes de implementar).

### Riscos / trade-offs
- **Consumidor front-end ainda não existe** (dependência cross-repo, LayoutParserReact) — isso não
  bloqueia o lado da API/MCP: a tool é útil standalone para qualquer agente MCP mesmo sem UI
  consumidora. Não esperar o front para desenhar/implementar este pedaço.
- `suggestedLayouts` com score é o componente de maior incerteza — se o mecanismo de matching
  interno não for facilmente extraível como operação isolada, o MVP pode sair só com
  `detectedType` (sem sugestão de layout), e a sugestão de layout vira issue de acompanhamento.
  Melhor entregar um MVP honesto do que atrasar por um score de confiança que não existe ainda.

### O que falta de decisão externa
Nenhum bloqueio técnico do lado da API — pronto pra implementar. Consumo pelo front-end é
trabalho separado, cross-repo (ver seção #221/#219/#218/#137 abaixo).

---

<a id="196"></a>
## #196 — Bug: colapso posicional LINHA006 (.mqseries)

### Problema real
Um bug específico de parsing posicional em documentos `.mqseries`, na "LINHA006", causando colapso
(provavelmente merge indevido de campos ou perda de posição). O dono já indicou que a investigação
real depende de um `correlationId` de um caso reproduzido em produção, que só ele pode fornecer.

### Desenho da investigação (mesmo sem o correlationId ainda)
Não precisamos ficar travados esperando o dado — dá pra preparar o terreno:

1. **O que precisamos quando o correlationId chegar:** log estruturado do request (já correlacionado
   via `CorrelationId` no `LogContext`, padrão já usado no MCP e na API), especificamente as
   entradas de log em torno de `LineInfo`/sinais aditivos (`IsDeclaredEmpty`,
   `PositionalAlignmentFailed` — já existem, ver `Models/Entities/LineInfo.cs` e o contrato recente
   de `contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md`) para a linha 6 do
   documento problemático, e — mais importante — o **arquivo `.mqseries` original** (ou pelo menos
   os bytes da linha 6, já que MQSeries tem nuances de linha variável, ver
   `idoc-textpositional-overload`) para reproduzir localmente.
2. **Como vamos usar o correlationId quando chegar:** grep estruturado nos logs
   (Serilog/Elastic, ou arquivo local se for ambiente dev) por `CorrelationId=<valor>`, extrair a
   sequência completa de parsing daquele request — de detecção de tipo até resultado final —, e
   isolar especificamente o processamento da linha 6.
3. **Hipótese de trabalho a validar assim que houver dado real:** "LINHA006" sugere um índice fixo
   de linha, não um nome de campo — vale checar se existe alguma lógica no parser MQSeries que trata
   a 6ª linha de forma especial (cabeçalho de bloco? campo de controle?) e se o "colapso" é
   consistente com o discriminador `WithBreakLines` (já identificado como fonte de bug em
   `idoc-textpositional-overload` — mesma classe de problema pode se repetir aqui: o parser assume
   uma característica estrutural que o layout declara mas não confere na prática).
4. **Preparar o terreno de teste:** já existe `tests/LayoutParserApi.Tests/Parsing/
   LineInfoAdditiveSignalsTests.cs` — o teste de regressão para #196, quando o bug for entendido,
   deve nascer como caso adicional aqui, não arquivo novo.

### Arquivos/serviços afetados
- `Services/Implementations/LayoutParserService .cs` (parser principal, nome com espaço no arquivo).
- `Models/Entities/LineInfo.cs`.
- `tests/LayoutParserApi.Tests/Parsing/LineInfoAdditiveSignalsTests.cs` (destino do teste de
  regressão futuro).

### Riscos / trade-offs
Nenhum risco de desenho — é bug de campo, a única coisa que trava é o dado de reprodução.

### O que falta de decisão externa
**Bloqueio real:** o `correlationId` (ou arquivo `.mqseries` de exemplo com o problema) só o dono
tem. Sem isso, não avança além da preparação acima.

---

<a id="221-219-218-137"></a>
## #221 / #219 / #218 / #137 — Epic FIAT + auth m2m + mapeamento campo (cross-repo)

Estas quatro têm um núcleo comum: dependem do LayoutParserReact para fechar o ciclo, mas a parte
que cabe à API já pode ser desenhada.

### #218 — gate: sem mecanismo de autenticação m2m
**Problema:** hoje a identidade vem só do BFF via headers confiáveis (`TrustedIdentityMiddleware`,
guarda de loopback) — não existe caminho de autenticação **máquina-a-máquina** (ex.: um pipeline
Cypress e2e, ou um serviço automatizado do lado FIAT, chamando a API diretamente sem passar pelo
BFF/usuário humano).
**Abordagem recomendada:** client credentials flow simples via Entra ID (mesmo provedor OIDC já
usado pelo BFF, conforme `security.md`) — a API valida um JWT de aplicação (não de usuário) num
novo middleware `M2mAuthenticationMiddleware`, paralelo ao `TrustedIdentityMiddleware` (não
substituindo-o: tráfego do BFF continua via headers confiáveis + loopback; tráfego m2m externo usa
JWT). Escopo mínimo: um único "client" registrado no Entra para o pipeline Cypress/FIAT, sem
matriz de múltiplos clients ainda.
**Afeta:** `Services/Security/` (novo middleware), `Program.cs` (pipeline de auth), `appsettings.json`
(config do tenant/client Entra — nunca segredo em texto plano, usar user-secrets/env var).
**Decisão externa:** confirmar com o dono se Entra ID client-credentials é aceitável ou se FIAT
exige um mecanismo próprio (certificado mTLS, API key dedicada) — muda a implementação por completo.

### #219 — gate: generate-for-layout recusa layout FIAT
**Problema:** decorre de #218 — sem m2m, o endpoint de geração não tem como saber que a chamada é
legítima e vinda do pipeline FIAT, então recusa. Uma vez #218 resolvido, #219 é consequência
natural: o gate de recusa vira uma checagem de claim específica no JWT m2m (`aud`/`scope` contendo
"fiat" ou client id específico) no filtro de autorização do endpoint `generate-for-layout`.
**Não é trabalho novo de desenho** além do que #218 já cobre — é a aplicação do mecanismo.

### #221 — epic guarda-chuva
Agrega #218+#219 (lado API) + validação Cypress e2e (lado React, fora do escopo da API). Do lado
API, o "pronto" de #221 é #218+#219 fechados; o Cypress e2e é trabalho do LayoutParserReact.

### #137 — story: mapeamento campo TXT↔XML (depende de front-end)
**Problema:** expor de forma navegável qual campo do TXT posicional corresponde a qual elemento do
XML — hoje esse mapeamento existe internamente (`FieldMappingCompositionService`, usado no parse),
mas não como um contrato de API pensado para consumo por UI interativa (ex.: usuário clica num
campo do TXT e vê destacado o elemento XML correspondente, e vice-versa).
**O que cabe à API desenhar agora:** o contrato de resposta. Recomendo
`GET /api/parse/{parseId}/field-mapping` retornando:
```json
{
  "mappings": [
    { "txtField": { "line": 6, "startCol": 10, "endCol": 25 }, "xmlPath": "/NFe/infNFe/det[1]/prod/xProd" }
  ]
}
```
reaproveitando o mesmo `FieldMappingCompositionService` que já produz essa associação internamente
— o trabalho da API é **expor**, não recalcular. A parte de UI (destacar/clicar) é 100%
LayoutParserReact, fora do escopo deste desenho.
**Decisão externa:** nenhuma bloqueante do lado API — pode ser implementado independente do front,
e o front consome quando estiver pronto.

---

<a id="112"></a>
## 🎯 #112 — `MIGRATE_CONFIG_TO_REPO=true`

Depende de B1+A1+A2 (trilhas já em andamento/fechadas conforme memória). Decisão do dono: **quando**
ativar a flag, não **como** — o mecanismo técnico já existe (é feature-flag de migração de config).
Opções:
- **(A) Ativar assim que A1+A2 confirmarem estáveis em dev** — menor risco, mas atrasa o benefício.
- **(B) Ativar já, com rollback manual documentado se algo quebrar** — mais rápido, exige runbook
  de rollback pronto antes de ligar (que hoje pode não existir — confirmar antes de escolher B).

---

<a id="110"></a>
## 🎯 #110 — Dry-run "config drift" contra produção

Decisão do dono: qual **canal de execução** para o dry-run.
- **(A) Manual, sob demanda** (`dotnet run --project tools/ConfigDriftTool -- --dry-run`) —
  zero automação, zero risco de rodar sem querer contra produção.
- **(B) Step de CI agendado** (ex.: cron semanal no GitHub Actions) — visibilidade contínua, mas
  exige credencial de leitura de produção acessível ao runner (superfície de risco extra, ver
  histórico de segredo comprometido no projeto).
- Recomendação implícita (não decisão fechada): (A) primeiro, (B) só depois que (A) provar valor
  repetidamente — mas a escolha final é do dono.

---

<a id="108"></a>
## 🎯 #108 — Drift appsettings.json prod vs repo

Pré-requisito de #110 (a ferramenta de dry-run de #110 é o que detecta este drift). Decisão do
dono: **o que fazer quando um drift for encontrado**.
- **(A) Só alertar** (e-mail, reaproveitando o mecanismo já implementado em `deploy.yml` para
  alerta de deploy — ver `security.md` §"Alerta de deploy por e-mail") — dono decide manualmente
  se reconcilia repo↔prod.
- **(B) Gerar PR automático** propondo reconciliar o repo com o valor de produção (nunca o
  inverso — produção nunca deve ser sobrescrita automaticamente) — mais proativo, mas introduz
  automação escrevendo em `appsettings.json`, que já teve duas regressões de segredo neste projeto;
  exige filtro rígido pra nunca incluir valores que pareçam segredo no PR automático.

---

<a id="96"></a>
## 🎯 #96 — `FindXslFile`: parâmetros mortos

Lendo `Services/XmlAnalysis/TransformationPipelineService.cs`: `FindXslFile(sourceType, targetType,
layoutName)` é chamado em dois pontos — um com `sourceType`/`targetType` reais (linha 109) e outro
sempre com `"Intermediate"` fixo (linha 311). Decisão do dono é só sobre **agressividade da
limpeza**:
- **(A) Remover os parâmetros mortos agora** (checar dentro do método se `sourceType`/`targetType`
  realmente influenciam a busca do arquivo, ou só aparecem em log — se só logam, são mortos de
  verdade) — tech-debt fechado rápido, baixo risco, coberto por
  `TransformationPipelineServiceMapFileTests.cs` já existente.
- **(B) Deixar como está até #173 ser feito** — já que #173 mexe na mesma vizinhança de validação/
  transformação, pode valer a pena fazer os dois juntos para evitar dois PRs pisando no mesmo
  arquivo em sequência curta.

---

<a id="88"></a>
## 🎯 #88 — 27(28) achados SecurityCodeScan sem rastreamento

`security-code-scan-baseline.json` já existe e está sendo mantido ativamente (commits recentes
`b23555a`, `fb9f3d4`, `3a0f3fa` mostram atualização contínua do baseline conforme código muda) —
o mecanismo de supressão funciona. O que falta é **rastreamento formal** dos achados aceitos.
Decisão do dono:
- **(A) Uma issue por achado** (27-28 issues novas) — rastreamento granular, mas infla o board.
- **(B) Uma issue guarda-chuva** ("SCS baseline — achados aceitos") com checklist interno,
  revisada periodicamente — mais leve, menos visibilidade individual.
- **(C) Nenhuma issue, só comentário inline no baseline/código** (já parcialmente feito — o
  `TransformationValidatorService.cs` já tem comentários `// ✅ SCS0018: ...` justificando supressões
  pontuais) — zero overhead de board, mas sem rastreamento centralizado se o dono quiser auditar
  todos de uma vez.
- Recomendação implícita: (C) já é parcialmente a prática atual; (B) é o meio-termo mais barato se
  o dono quiser visibilidade sem 28 issues.

---

## Notas finais de consistência

- **#97 pode já estar parcialmente resolvida** por `feat/ai-user-session-schema-102` (mergeada
  recentemente, PR #270) — verificar antes de iniciar implementação para não recriar schema.
- **#90 e #173 ambas tocam a fronteira "capacidade anunciada vs capacidade real"** — vale
  sequenciar #90 antes de #173, já que um gate de capacidade no boot pode revelar que alguma
  dependência do `TransformationValidatorService` (ex.: XSD path, TCL path) já está sujeita ao
  mesmo tipo de falha silenciosa que #90 quer capturar.
- **#96 e #173 tocam o mesmo arquivo em sequência curta** (`TransformationPipelineService.cs` /
  `TransformationValidatorService.cs`, mesma pasta) — considerar um único PR ou PRs sequenciados
  próximos para reduzir conflito de merge.
