# ADR — Contrato de correção guiada por humano (LayoutParserReact#232/#234)

Autor: `@lp-architect` (Aria). Pedido cross-repo do time React, mesmo padrão de #198/#201.
Análise e design de contrato — **sem implementação**.

## 0. Confirmação inicial

Não existe hoje nenhum endpoint de escrita, nenhum `DocumentId` estável no domínio, nenhum
contrato desse tipo. É desenho genuinamente novo — não lacuna de algo já especificado. O que
já existe e serve de base: o loop `RepairOrchestrator` (gerar → validar via `CanonicalDiffer`
+ XSD → corrigir com Ollama, ver
`docs/architecture/adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md`, branch
`docs/adr-convergencia-tcl-xslt-151`), o hook `TrainingDataCaptureService` (F3, issue #338,
grava JSONL `instruction/input/output` compatível com `train_lora.py`), e o padrão de
identidade `ICurrentUser`/`TrustedIdentityMiddleware` já usado em toda a API.

## 1. Pergunta 1 — existe/está planejado endpoint de escrita? Qual shape?

**Não existe. Proponho criar `POST /api/transformation/field-correction`** (mesmo controller
`TransformationExecutionController`, mesmo grupo de rotas que já expõe `execute-candidates` e
`field-mappings`).

O payload do front é um bom ponto de partida, mas precisa de 3 ajustes:

```json
{
  "documentId": "doc_9f3a...e21",          // NOVO — ver pergunta/gap 1
  "correlationId": "corr-do-execute-candidates",
  "layoutGuid": "LAY_...",
  "candidateId": "tclxsl-1",
  "pathway": "tcl-xsl",
  "fieldPath": "/nfeProc/NFe/infNFe/ide/nNF",
  "observedValue": "001",
  "expectedValue": "1",
  "justification": "Não deveria ter zero à esquerda (regra fiscal X).",
  "reportedAt": "2026-09-08T12:00:00Z"     // pode ser omitido — o servidor carimba, ver 5.2
}
```

Removido do payload deles: `documentType` — já é derivável do `layoutGuid` via
`XmlDocumentTypeDetector`/catálogo, não precisa vir do cliente (reduz superfície de
inconsistência: se o cliente mandar `documentType` errado para um `layoutGuid` certo, qual
prevalece?). Mantenho como campo **opcional só para telemetria/log**, nunca como fonte de
verdade.

Resposta HTTP (`202 Accepted`, ver pergunta 3 — é ingestão assíncrona):

```json
{
  "reportId": "guid",
  "status": "queued",
  "message": "Correção registrada. Será usada para refinar o modelo em treinos futuros."
}
```

## 2. Pergunta 2 — falta algo no payload deles?

Sim, dois campos, ambos derivados de infraestrutura que já existe (não é campo novo pro
usuário preencher, é o backend que precisa resolver e persistir):

- **`mapperGuid` / `mapperName`** — o payload deles tem `layoutGuid` mas o
  `RepairOrchestrator`/`TrainingDataCaptureService` chaveiam por **mapper**, não por layout
  (um layout pode ter mais de um mapper candidato, `candidateId` sugere isso: `tclxsl-1`). Sem
  `mapperGuid`, o backend não sabe qual par `(input, groundTruthXml, XSLT)` corrigir — resolve
  isso no servidor a partir de `candidateId` + `correlationId` (ver 5.1), não pede ao cliente.
- **`groundTruthXml` já resolvido** (o Sysmiddle daquela execução) — **não precisa vir no
  payload**. Já está persistido implicitamente enquanto o `correlationId` ainda for
  resolvível (ver gap `documentId`, Seção 4) — outra razão para exigir `documentId` estável em
  vez de reconstruir o contexto a partir de `correlationId` efêmero.

Não preciso de versão de layout nem hash do documento original como **campo explícito do
payload** — ambos ficam resolvidos a partir do `documentId` proposto (Seção 4), que já
amarra num único identificador tudo que o `RepairOrchestrator` precisou para gerar o
candidato que está sendo corrigido.

## 3. Pergunta 3 — síncrono ou assíncrono?

**Assíncrono.** Justificativa:

- O propósito da correção humana não é "re-executar agora e devolver um XML melhor na hora"
  — é **ensinar o próximo treino/convergência**. Não há um consumidor síncrono esperando essa
  resposta: o usuário já está vendo o resultado (certo ou errado) na tela, o reporte é sobre
  o *próximo* documento, não o atual.
- Fazer síncrono obrigaria rodar uma nova iteração do `RepairOrchestrator` (chamada real ao
  Ollama, ~segundos a dezenas de segundos, ver `RepairOrchestrator.RunAsync`) dentro do
  request HTTP do usuário só para confirmar que a correção "funcionou" — caro e frágil (mesma
  classe de risco que o resto do projeto evita: dependência externa, Ollama, no caminho
  síncrono de resposta ao usuário, contra o princípio de resiliência de
  `.claude/rules/dotnet-standards.md`).
- Resposta ao usuário: `202 Accepted` com `"Correção registrada. Será usada para refinar o
  modelo em treinos futuros."` — sem promessa de efeito imediato. É honesto sobre o que
  realmente acontece (vira dado de treino incremental, não corrige o XML que ele está vendo
  agora).

**Consequência de design:** o handler do endpoint só valida, resolve `mapperGuid`/contexto, e
persiste — não chama `IXslSynthesizerService`/Ollama no request. A entidade persistida
(Seção 5) é o material bruto que um processo futuro (F3 batch, ou uma frase nova
`RepairFromHumanCorrectionAsync`) consome depois, fora do ciclo de request-response.

## 4. Gap 1 — `documentId` estável

**Não existe hoje.** Investigação: o único identificador ligado a uma execução de
`execute-candidates` é `CorrelationId` (`Services.Logging.CorrelationContext.CurrentId`),
gerado por request HTTP — não sobrevive além daquele request/response, não é persistido em
lugar nenhum, não pode ser reusado minutos depois para "peço a correção do documento que vi
há pouco". `ParsedField`/`LayoutRecord` também não carregam um identificador estável do
documento — o pipeline é stateless por design (parse → resposta, sem sessão).

**Proposta: gerar um `DocumentId` derivado de hash no momento de `execute-candidates`,
devolvê-lo na resposta, e persistir o contexto mínimo associado (não o XML inteiro de novo —
seria duplicar dado).**

```
DocumentId = "doc_" + SHA256(InputContent + "|" + resolvedLayoutGuid)[:16]  // hex truncado
```

Determinístico (mesmo documento + mesmo layout sempre gera o mesmo `DocumentId` — reenviar o
mesmo TXT não cria registros duplicados desnecessários) e não exige tabela de sessão nem
estado novo no pipeline de parse. Contrato:

1. `execute-candidates` calcula e inclui `documentId` na
   `TransformationExecutionCandidatesResponse` (ao lado de `CorrelationId` já existente).
2. Uma tabela nova, pequena, `tbFieldCorrectionContext` (banco `IdentityDatabase`, ver regra
   `172.31.249.51` é somente-leitura em `.claude/rules/security.md`) grava, best-effort,
   `(DocumentId, MapperGuid, MapperName, LayoutGuid, LayoutName, GroundTruthXml, InputXml,
   CreatedAtUtc)` no mesmo ponto onde hoje `TryEnqueueAiCandidate`/candidatos sysmiddle já
   resolvem esses dados — é gravação adicional de contexto já calculado, não recomputação.
   TTL/retenção: fora de escopo deste ADR, recomendo aos donos de implementação um cron de
   limpeza (30-90 dias) análogo ao já usado para dados efêmeros do projeto.
3. `POST field-correction` recebe `documentId`, busca essa linha — se não existir (expirado,
   ou nunca foi gravado por falha best-effort), responde `404` com mensagem clara ("contexto
   do documento expirado — reenvie o parse para reportar uma correção").

Isso é estritamente aditivo sobre `TransformationExecutionController`: não muda nenhum
contrato existente, só acrescenta um campo na resposta e um hook de persistência best-effort
(mesmo padrão de `TryPersistXslt`/`TrainingDataCaptureService.TryCapture` — nunca falha o
request principal).

## 5. Gap 2 — autoria do reporte

**Confirmado: `ICurrentUser`/`TrustedIdentityMiddleware` já é o padrão certo, nada novo.**
Mesmo mecanismo usado em `MappingGovernanceController` (`_currentUser.UserId is not Guid
userId → NotFound()`, fail-closed) — aplico igual aqui:

```csharp
[HttpPost("field-correction")]
public async Task<IActionResult> ReportFieldCorrection(
    [FromBody] FieldCorrectionRequest request, CancellationToken cancellationToken)
{
    if (_currentUser.UserId is not Guid userId)
        return NotFound(); // fail-closed, mesmo padrão de MappingGovernanceController

    // ...resolve contexto via documentId, persiste com ReportedByUserId = userId...
}
```

Sem gate de papel (`RequireWorkspaceRole`) nesta primeira versão — qualquer usuário
autenticado que viu o resultado do parse pode reportar uma divergência; é entrada de dado
para treino, não uma operação privilegiada de governança (diferente de aprovar/publicar
release). Se o volume de reportes ruidosos/mal-intencionados virar problema real, revisitar.

`reportedAt` do payload do front é redundante — o servidor carimba
`CreatedAtUtc = DateTime.UtcNow` na persistência (mesmo padrão de
`TrainingDataCaptureService.BuildJsonlLine`); mantenho o campo como opcional só para o front
mostrar "enviado às HH:mm" otimisticamente antes da resposta do servidor chegar, sem
depender do valor no armazenamento.

## 6. Pergunta implícita — critério de aceite é o mesmo "1:1 contra o Sysmiddle"?

**Não, é fundamentalmente diferente — e essa diferença precisa estar explícita no desenho,
não assumida.**

O loop automático (`RepairOrchestrator`, F1/F2 do ADR de convergência) tem um oráculo único e
inquestionável: o Sysmiddle. `CanonicalDiffer.Diff(actual, groundTruthXml) == 0` é a definição
de "certo". Correção humana **quebra essa premissa por definição**: o usuário só abre o
formulário quando acha que o Sysmiddle (ou o candidato gerado) está errado — ele está,
explicitamente, discordando do oráculo. No exemplo do dono, `<nNF>001</nNF>` (o que saiu) vs
`<nNF>1</nNF>` (o que o usuário diz que deveria sair): **não há como o sistema saber qual dos
dois é o certo sem um segundo oráculo** — nem o `CanonicalDiffer` nem o XSD schema resolvem
isso (ambos aceitariam qualquer um dos dois como "bem formado"; a regra de zero-padding em
`nNF` é uma regra de negócio fiscal, não estrutural).

**Consequência de design, três decisões:**

1. **O reporte NUNCA é aplicado automaticamente como correção de verdade.** Ele vira
   `PendingHumanReview`, não `Converged`. Aplicá-lo direto ao dataset de treino como se fosse
   gabarito validado (mesmo nível de confiança do Sysmiddle) contaminaria o dataset com
   possíveis erros do usuário — sem segundo oráculo, o sistema não tem como distinguir "usuário
   certo, Sysmiddle errado" de "usuário errado, Sysmiddle certo".
2. **O registro persistido carrega os DOIS valores lado a lado** (`observedValue` +
   `expectedValue` + `fieldPath` + o par `input/groundTruthXml` completo via `documentId`) —
   nunca sobrescreve o `groundTruthXml` original. Isso preserva a possibilidade de um humano
   com mais autoridade (revisor fiscal) decidir depois qual dos dois estava certo, sem perder
   informação.
3. **Consumo no treino é revisão humana obrigatória antes de virar exemplo de treino**, não
   ingestão direta como o F3 automático faz com convergências do `RepairOrchestrator`. Proponho
   uma fila de curadoria simples: `tbFieldCorrectionReport` com status
   `pending → reviewed_accepted | reviewed_rejected`, e só `reviewed_accepted` alimenta o
   JSONL incremental (schema idêntico ao de `TrainingDataCaptureService`, com
   `"source": "human-correction-reviewed"` em vez de `"repair-orchestrator-runtime"` — o campo
   `source` já existe no schema, é diferenciação livre). A revisão em si (quem aprova, UI de
   curadoria) é decisão de produto fora do escopo deste ADR — só fixo aqui que ela é
   **obrigatória antes do reporte virar dado de treino**, exatamente porque a correção humana
   não tem o mesmo grau de confiança automática que a convergência 1:1 contra o Sysmiddle.

## 7. Contrato final (resumo executável)

**Endpoint:** `POST /api/transformation/field-correction` — `TransformationExecutionController`.

**Request (`FieldCorrectionRequest`):**

| Campo | Tipo | Obrigatório | Origem |
|---|---|---|---|
| `documentId` | string | Sim | Devolvido por `execute-candidates` (novo campo na resposta) |
| `candidateId` | string | Sim | Já devolvido por `execute-candidates` |
| `fieldPath` | string | Sim | XPath do campo divergente |
| `observedValue` | string | Sim | Valor que saiu |
| `expectedValue` | string | Sim | Valor que o usuário espera |
| `justification` | string | Não | Texto livre |
| `documentType` | string | Não (telemetria) | — |

**Response:** `202 Accepted`, `{ reportId, status: "queued", message }`. `404` se
`documentId` não resolver contexto persistido. `401`/fail-closed via `_currentUser.UserId`.

**Persistência:**
- `tbFieldCorrectionContext` (best-effort, gravado em `execute-candidates`) —
  `DocumentId, MapperGuid, MapperName, LayoutGuid, LayoutName, InputXml, GroundTruthXml, CreatedAtUtc`.
- `tbFieldCorrectionReport` — `ReportId, DocumentId, CandidateId, FieldPath, ObservedValue,
  ExpectedValue, Justification, ReportedByUserId, Status (pending/reviewed_accepted/
  reviewed_rejected), CreatedAtUtc, ReviewedByUserId?, ReviewedAtUtc?`.
- Ambas em `IdentityDatabase:*` (nunca `172.31.249.51`), seguindo o padrão de
  `SqlMappingDraftStore`/`FiscalSchemaInitializer`.

**Mudanças aditivas em código existente:**
- `TransformationExecutionCandidatesResponse` ganha `DocumentId`.
- `TransformationExecutionController.ExecuteCandidatesAsync` calcula o hash e persiste
  `tbFieldCorrectionContext` best-effort (mesmo try/catch de `TryPersistXslt`).
- Novo endpoint `ReportFieldCorrection` no mesmo controller.
- Fila de curadoria (`reviewed_accepted` → JSONL) é peça separada, fora deste ADR — recomendo
  issue própria (Seção 8).

## 8. Recomendação de issue (para `@lp-pm`)

Recomendo **duas issues**, não uma:

1. **Endpoint + persistência de contexto** (`documentId`, `tbFieldCorrectionContext`,
   `tbFieldCorrectionReport`, `POST field-correction`) — implementação de
   `@lp-backend-dev`, referenciando este ADR e LayoutParserReact#232/#234.
2. **Fila de curadoria + consumo no treino incremental** (`reviewed_accepted` → JSONL,
   possivelmente uma tela/endpoint de revisão) — depende da issue 1, escopo de
   `@lp-parser-llm`, pode entrar depois sem bloquear o endpoint de reporte em si (o usuário já
   consegue reportar mesmo antes de existir curadoria — os dados só ficam represados em
   `pending` até a curadoria existir).

Não recomendo issue única — front consegue integrar contra a issue 1 sozinha (o contrato
HTTP já fecha o loop de UX deles: reportar → `202` → mensagem de confirmação), a curadoria é
back-office e não bloqueia a entrega de valor imediata.

---
*ADR de `@lp-architect` — análise e design de contrato, sem implementação. Push/PR e criação
de issue ficam com `@lp-devops`/`@lp-pm`.*
