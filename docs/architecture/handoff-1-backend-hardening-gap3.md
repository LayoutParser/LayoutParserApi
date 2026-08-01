# Handoff 1/3 — Hardening do backend (Gap 3: métricas de IA)

> Para uma sessão nova de Claude Code, repo `LayoutParserApi`. Escrito por `@lp-architect` (Aria),
> 2026-07-31, consolidando achados de `@lp-qa` (Quinn) e `@lp-backend-dev` (Dex) desta sessão.
> Agente sugerido: `@lp-backend-dev`, com `@lp-qa` fazendo o gate no final.

## Contexto que você precisa saber antes de começar

O Gap 3 (painel de métricas de IA, `docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md`)
está em produção e funcionando: `Controllers/AiMetricsController.cs`,
`Services/Logging/AiMetricsReaderService.cs`, `Services/Logging/AiMetricsIngestService.cs`. Um
QA gate completo já rodou (2 rodadas) e corrigiu 6 bugs de corrupção de dado — todos verificados
por execução, não só leitura. **Não repita esse trabalho.** Este handoff é só a dívida que
sobrou, classificada como "não bloqueia, mas precisa ser feita".

`master` está sincronizada com `origin`. Rode `git log --oneline -5` e `git status -sb` antes de
tocar em qualquer coisa — pode haver outra sessão trabalhando na mesma árvore (aconteceu 2x nesta
semana). Se houver working tree sujo que não é seu, não presuma que pode descartar.

## Item 1 — Endpoint `/ingest` sem autenticação (prioridade mais alta dos 3 itens abaixo)

`Controllers/AiMetricsController.cs` — nem `[Authorize]` nem `[AllowAnonymous]` estão presentes
em nenhum método (confirmado por grep). O projeto inteiro tem `UseAuthorization` comentado em
`Program.cs` — isso não é regressão desta feature, é postura pré-existente. Mas este é um
endpoint de **escrita** que alimenta um painel mostrado à diretoria: qualquer um na rede injeta
métrica falsa sem autenticação nenhuma.

**Não implemente OIDC/JWT agora** — seria desproporcional ao resto do projeto (que não tem auth
em lugar nenhum) e fora do escopo deste handoff. Solução proporcional: restringir por IP de
origem (a VM que legitimamente chama este endpoint tem IP conhecido, hoje `172.25.32.3` — mas
**leia o item 3 antes de hardcodar esse IP**, ele já mudou 3 vezes). Considere middleware simples
ou `app.MapPost(...).RequireAuthorization(...)`  com policy por IP, ou um header de chave
compartilhada simples (não é segurança forte, mas é proporcional ao resto do projeto e barato).
Decida e documente a escolha — não precisa ser perfeito, precisa ser melhor que "aberto".

## Item 2 — Dois bugs no `AiMetricsIngestService` (achados do QA, não bloquearam produção)

**A1 — idempotência quebra sem `Timestamp`.** `Services/Logging/AiMetricsIngestService.cs:149`,
método que normaliza timestamp: cai em `DateTime.Now` quando o campo vem ausente no payload. O
contrato (`Models/Logging/AiMetricsModels.cs:168`) permite omitir. Medido pelo QA: a mesma
geração enviada 2x sem `Timestamp` → 2 itens distintos no painel (timestamps
`11:35:29.505`/`11:35:29.569`). O XML doc de `Controllers/AiMetricsController.cs:146` promete
"Reenviar o mesmo lote é seguro" sem essa ressalva — está mentindo hoje.

Correção: ou (a) exigir `Timestamp` no payload — 400 se ausente — ou (b) corrigir o XML doc para
documentar a limitação real. Prefira (a): idempotência que depende de sorte de timing não é
idempotência.

**A2 — sem teto de tamanho de campo.** `AiMetricsIngestService.cs:82-94`, método `ValidarItem`:
checa só null/whitespace, não tamanho. QA testou `Layout` com 200.000 caracteres — foi aceito e
gravou 200.313 bytes numa única linha de log. O endpoint irmão `cypress-result` já tem o padrão
certo (`AiMetricsController.cs:25-27`, tetos de 500/20/1000 chars). Aplique o mesmo padrão aqui.
Motivo concreto: a retenção de log é limitada (~20 MB, `RetainedFileCountLimit`/`FileSizeLimitKB`
em `appsettings.json`) e o leitor só abre os 3 arquivos mais recentes por fonte — um payload
gigante evicta histórico real de gerações.

## Item 3 — IP da VM hardcoded em 4 comentários XML doc, e o IP muda sozinho

Achado operacional desta sessão: o IP da VM de métricas mudou **3 vezes por DHCP em 2 semanas**
(`.30` → `.31` → `.3`). Os arquivos abaixo ainda citam `172.25.32.31`:

- `Controllers/AiMetricsController.cs:137`
- `Models/Logging/AiMetricsModels.cs:143`
- `Services/Logging/AiMetricsIngestService.cs:11`
- `Services/Interfaces/IAiMetricsIngestService.cs:7`

São só comentários (XML doc), não afetam execução — mas confundem quem ler depois. **Não troque
simplesmente por `.3`** — vai ficar errado nas próximas semanas de novo. Prefira uma das duas
saídas: (a) trocar o IP literal por "a VM de métricas de IA (ver runbook operacional)" sem
número, ou (b) se quiser manter um IP de referência, adicione uma nota "(o IP muda por DHCP,
confirme o atual antes de usar)". Avise `@lp-devops` que uma reserva de IP fixo por MAC
resolveria isso na raiz — é fora do seu escopo, é só o comentário certo aqui.

## Item 4 — Campo `Observacao` write-only

`Models/Logging/AiMetricsModels.cs` — `AiMetricsCypressResultRequest.Observacao` existe no
request do endpoint `POST /api/ai-metrics/cypress-result`, é sanitizado e gravado no log, mas
**nunca aparece** em `AiMetricsGeneration` nem em nenhum contrato de leitura. O handoff original
dizia que serviria "pro painel mostrar contexto" — hoje ela só existe para ser escrita, nunca
lida. Decisão sua: ou (a) expor o campo (adicionar em `AiMetricsGeneration`, no parse do reader,
e documentar no contrato do front-end — coordene com quem estiver no Handoff 2), ou (b) remover
da mensagem de log já que não tem consumidor — mais simples, e reduz a superfície de sanitização
necessária. Se optar por (b), o `LogMessageSanitizer` compartilhado (`Services/Logging/`) fica
mais simples também.

## Item 5 — Zero cobertura de teste automatizado (o item que mais protege o futuro)

Não existe projeto `*.Tests.csproj` no repositório — `dotnet test` não roda nada. Os 6 bugs de
corrupção de dado que o QA corrigiu nesta sessão (regex `[Corr:]`, geração fantasma via `\n` em
`Observacao`, cStat forjado via `=`, contagem de autorização errada, merge retroativo, merge
perdido por filtro de data) foram verificados por um harness **descartável**, fora do
repositório. Nada impede que voltem amanhã sem alarme nenhum no CI.

Crie `LayoutParserApi.Tests` (xUnit), cobrindo pelo menos:
1. `UnifiedLogReaderService` parseia linha sem `[Corr:]` (regex opcional).
2. `AiMetricsReaderService` não deixa `\n`/`=` em `Observacao` forjar campo (sanitização).
3. `AiMetricsReaderService` não conta rejeição (`cStat` não-100) como autorização.
4. Merge Cypress×geração não contamina rodadas antigas do mesmo `Layout` (limite superior de
   timestamp).
5. Merge não se perde quando o filtro `de`/`ate` corta a janela.
6. `AiMetricsIngestService`: item inválido no meio de um lote não derruba o lote inteiro.

Os cenários de referência (não o código, que era descartável) estão descritos nas memórias de
`@lp-qa`: `.claude/agent-memory/lp-qa/ai-metrics-gap3-qa-gate.md`. Adicione o projeto ao
`dotnet build`/CI (`ci-dev.yml` já roda `dotnet build`; adicionar `dotnet test` é responsabilidade
de `@lp-devops`, mas o projeto de teste em si é seu).

## Fora de escopo deste handoff

O Job 2 (Cypress/Pollux) tem gaps estruturais maiores — persistência de candidato, XSLT vs XML,
elegibilidade de 4/54 pares. Isso é o Handoff 3, não misture aqui.

## Antes de terminar

```bash
dotnet build
dotnet test    # depois de criar o projeto do Item 5
```

Commits Conventional, PT-BR nos comentários, estilo já presente no código. **Não faça `git
push`** — autoridade exclusiva de `@lp-devops`. Peça o gate final a `@lp-qa` antes de considerar
pronto.
