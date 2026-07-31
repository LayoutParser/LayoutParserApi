# Handoff para @lp-front-dev (LayoutParserReact) — Gap 3: Painel de métricas da IA

> Repo alvo: `LayoutParserReact`. Origem: `LayoutParserApi`, branch `develop`.
> Escrito por `@lp-architect` (Aria) — design de contrato, **sem código implementado ainda**
> (diferente dos Gaps 1/2, que já estavam prontos). Este documento serve para o front-end começar
> o desenho de tela em paralelo enquanto o backend implementa os dois endpoints abaixo.

## Contexto

O job de métricas de geração de IA (`ai/XslSynth --mode=metrics-batch`) já está rodando de verdade
em produção — na VM `172.25.32.31`, via `cron`, todo sábado 00:00, contra os 54 pares do dataset
`dataset_pairs_filtered_v2.jsonl` (NFe/CTe/MDFe). Cada geração e cada resumo de lote caem como log
estruturado (`Source=AiMetrics`) no mesmo arquivo já lido pelo `GET /api/logs` existente
(`UnifiedLogReaderService`).

**Objetivo do painel:** dar visibilidade real, sem depender de ninguém ler arquivo de log
manualmente, sobre o que a IA está gerando sem supervisão — throughput, qualidade estrutural, e
(quando a integração Cypress/Pollux existir) se o XML gerado realmente seria aceito pela SEFAZ.
Isso é a base de dado da apresentação ao coordenador/diretoria — o painel É o produto que mostra
"a IA está rodando, eis o resultado real, eis se compensa investir em infra melhor".

---

## Por que precisa de 2 endpoints novos (não dá para reusar `GET /api/logs` direto)

O log de métricas é **texto estruturado dentro de uma linha de log**, não JSON já pronto:

```
[2026-08-01 00:03:12.401] [INF] [Src:AiMetrics] Geracao concluida. Layout=CTe\2.00a\CTe200_CancCTe_NeogridToSefaz Modelo=qwen2.5-coder:7b TokensPorSegundo=3.685 TamanhoPromptChars=2140 DuracaoSegundos=277.8 SimilaridadeFewShot=0.9257 TagOverlapRatio=1.0 TextSimilarityRatio=0.8899 XsdValido=null CypressValidado=null CStatPollux=null Sucesso=True
```

`GET /api/logs` hoje devolve isso como **1 campo `Message` (string)** — o front teria que fazer
parsing de texto no cliente, frágil e fora do padrão do resto da API. Por isso o backend expõe
2 endpoints novos que já fazem esse parsing e devolvem JSON tipado.

---

## Endpoint 1 — Lista de gerações

**Endpoint:** `GET /api/ai-metrics/generations`
**Implementado por:** `@lp-backend-dev` (Dex) — ainda não implementado, contrato abaixo é o alvo.

### Query params

```
?page=1&pageSize=20&layout=CTe&modelo=qwen2.5-coder:7b&sucesso=true&de=2026-08-01&ate=2026-08-02
```

Todos opcionais — sem filtro, retorna a página mais recente primeiro (mais novo → mais antigo).

### Response

```json
{
  "success": true,
  "totalCount": 54,
  "page": 1,
  "pageSize": 20,
  "items": [
    {
      "layout": "CTe\\2.00a\\CTe200_CancCTe_NeogridToSefaz",
      "docType": "CTe",
      "modelo": "qwen2.5-coder:7b",
      "timestamp": "2026-08-01T00:03:12.401Z",
      "tokensPorSegundo": 3.685,
      "tamanhoPromptChars": 2140,
      "duracaoSegundos": 277.8,
      "similaridadeFewShot": 0.9257,
      "tagOverlapRatio": 1.0,
      "textSimilarityRatio": 0.8899,
      "xsdValido": null,
      "cypressValidado": null,
      "cStatPollux": null,
      "sucesso": true
    }
  ]
}
```

### Pontos de atenção para a UI

- **`docType` é derivado no backend** (primeiro segmento de `layout`, ex. `CTe`/`NFe`/`MDFe`) —
  não faça esse parsing no front, o campo já vem pronto.
- **`xsdValido`, `cypressValidado`, `cStatPollux` vêm `null` hoje, para TODO item.** Não é bug —
  são os 3 campos que o job já loga (ver `MetricsBatchRunner.LogCaso`) mas que só serão
  preenchidos quando: (a) validação XSD real for cabeada no job (`xsdValido`), e (b) a spec
  Cypress em modo batch (item pendente, ver seção "Próximos passos" abaixo) rodar os candidatos
  contra o Pollux (`cypressValidado`/`cStatPollux`). **Trate os 3 como "pendente", não como
  "falhou"** — badge neutro tipo "não avaliado ainda", não vermelho.
- **`sucesso: false`** significa que o Ollama não retornou saída utilizável para aquele caso (ex.
  timeout) — não confundir com validação ruim. Um caso pode ter `sucesso: true` e ainda assim
  `tagOverlapRatio` baixo (gerou algo, mas estruturalmente ruim).
- Ordenação padrão por `timestamp` desc — a UI deveria deixar claro que é uma série temporal
  (gráfico de linha de `tokensPorSegundo`/`tagOverlapRatio` ao longo das rodadas de sábado é o
  visual mais forte para a apresentação).

---

## Endpoint 2 — Resumo agregado (para os cards do topo do painel)

**Endpoint:** `GET /api/ai-metrics/summary`
**Implementado por:** `@lp-backend-dev` (Dex) — ainda não implementado.

### Query params

```
?de=2026-08-01&ate=2026-08-31
```

Opcionais — sem filtro, agrega tudo que existir no log.

### Response

```json
{
  "success": true,
  "totalGeracoes": 54,
  "totalSucesso": 51,
  "totalFalhas": 3,
  "tokensPorSegundoMedio": 3.71,
  "tagOverlapMedio": 0.889,
  "textSimilarityMedia": 0.896,
  "totalXsdValidado": 0,
  "totalCypressValidado": 0,
  "totalCStatAutorizado": 0,
  "porDocType": [
    { "docType": "NFe", "total": 33, "sucesso": 32, "tokensPorSegundoMedio": 3.8 },
    { "docType": "CTe", "total": 15, "sucesso": 14, "tokensPorSegundoMedio": 3.6 },
    { "docType": "MDFe", "total": 6, "sucesso": 5, "tokensPorSegundoMedio": 3.7 }
  ],
  "ultimaRodada": "2026-08-01T03:15:00Z"
}
```

### Pontos de atenção para a UI

- `totalXsdValidado`/`totalCypressValidado`/`totalCStatAutorizado` vão ficar em `0` até os campos
  correspondentes do endpoint 1 começarem a ser preenchidos — mesma lógica: não é erro, é "ainda
  não chegamos nessa etapa do loop gerar→validar(XSD)→validar(Cypress/Pollux)".
- `ultimaRodada` serve para a UI mostrar "última execução: sábado, 03:15" — importante para
  transmitir que o job está vivo, não é uma foto estática.

---

## Contrato para quando o Cypress em modo batch existir (não é bloqueio, é preparação)

Quando a spec Cypress ganhar suporte a rodar uma lista de XMLs candidatos (item ainda pendente,
depende de destravar os artefatos de produção — `Mapeamentro`/`LayoutParserLowCodeRunner.exe` —
no ambiente onde a spec roda), os campos `xsdValido`, `cypressValidado` e `cStatPollux` do
Endpoint 1 passam a vir preenchidos de verdade (`true`/`false` e o código `cStat` retornado pela
SEFAZ-fake/Pollux). **Não é necessária nenhuma mudança de contrato na UI para isso acontecer** —
os campos já existem no schema, só passam a ter valor real em vez de `null`. Ou seja: pode
implementar a UI hoje já preparada para os 3 estados (`null`/`true`/`false`) sem esperar o Cypress
ficar pronto.

---

## Adendo (2026-07-30) — Endpoint 3: ingestão de resultado do Cypress/Pollux

**Contexto:** o Cypress em modo batch (spec adaptada de `ndd-api-central-cypress`, reaproveitando
`substituirValoresXML`/`ConstrutorCDV` para vestir os candidatos gerados pela IA com a identidade
de teste — `cnpjEmitenteTeste` — antes de enviar ao Pollux) roda **fora do processo da API**, na
mesma VM Ubuntu do job de métricas (`172.25.32.31`), headless (Cypress roda sem browser real em
Linux/CI normalmente — não precisa de UI). A API nunca dispara o Cypress; é só um consumidor
passivo dos resultados. Fluxo:

```
metrics-batch (gera + valida XSD, já em produção)
   → Cypress batch (lê candidatos xsdValido=true via GET /api/ai-metrics/generations,
      substitui chave/CNPJ de teste, envia ao Pollux, lê cStat)
   → POST /api/ai-metrics/cypress-result (grava de volta no log AiMetrics)
```

**Endpoint:** `POST /api/ai-metrics/cypress-result`
**Implementado por:** `@lp-backend-dev` (Dex) — a implementar.

### Request

```json
{
  "layout": "CTe\\2.00a\\CTe200_CancCTe_NeogridToSefaz",
  "cypressValidado": true,
  "cStatPollux": "100",
  "observacao": "string | null"
}
```

- `layout` identifica de forma inequívoca qual geração está sendo atualizada — deve casar
  exatamente com o campo `Layout` já logado pelo `metrics-batch` para aquele caso (mesmo valor
  que aparece em `GET /api/ai-metrics/generations`).
- `cypressValidado`: `true` = Pollux aceitou (cStat de autorização); `false` = rejeitado.
- `cStatPollux`: código retornado pela SEFAZ-fake/Pollux, como string (ex. `"100"`, `"110"`).
- `observacao`: opcional, texto livre (ex. motivo de rejeição) — útil pro painel mostrar contexto.

### Response

```json
{ "success": true }
```

### Implementação (orientação para o Dex)

- **Não é para reprocessar/reparsear o log inteiro** — a forma mais simples e correta de "gravar de
  volta" é o endpoint logar uma NOVA entrada Serilog `Source=AiMetrics`, mensagem tipo
  `"Cypress validado. Layout={Layout} CypressValidado={CypressValidado} CStatPollux={CStatPollux}"`.
  O `AiMetricsReaderService` (implementado no Gap 3) precisa então, ao montar cada
  `AiMetricsGeneration`, **fazer merge**: para cada `Layout`, se existir uma entrada posterior de
  "Cypress validado" com o mesmo `Layout`, sobrescrever `cypressValidado`/`cStatPollux` do
  resultado original (que nasceu `null`). Isso evita reescrever o arquivo de log (append-only,
  mesmo padrão já usado em todo o projeto) — a "atualização" é lógica, feita na leitura, não física.
- Sem autenticação especial — mesma superfície dos outros endpoints deste controller. Se o Cypress
  rodar de fato só na VM interna (rede fechada), risco de abuso é baixo; não é necessário adicionar
  proteção extra para esta primeira versão.
- Validação de entrada: 400 se `layout` vazio. Não é necessário validar que o `layout` existe no
  histórico (idempotente — se não existir ainda, a entrada de merge simplesmente não casa com
  nada, sem erro).

### Pontos de atenção para a UI

Nenhuma mudança de contrato nos Endpoints 1/2 já existentes — `cypressValidado`/`cStatPollux`
passam a vir preenchidos (em vez de `null`) automaticamente quando esse novo endpoint for chamado
para aquele `layout`. A UI já está preparada para os 3 estados, conforme o handoff original.

---

## Resumo para ação imediata

1. Front-end pode desenhar a tela do painel agora, contra este contrato — os 2 endpoints ainda
   não existem no backend (serão implementados por `@lp-backend-dev`).
2. Tela sugerida: cards de resumo (Endpoint 2) no topo + tabela/gráfico de série temporal
   (Endpoint 1) abaixo, com filtro por `docType`/`modelo`/período.
3. Tratar `xsdValido`/`cypressValidado`/`cStatPollux` = `null` como estado "pendente", não erro —
   em toda a UI, não só na tabela.
4. Nenhuma migração necessária dos Gaps 1/2 (já entregues) — este é um contrato adicional,
   independente.
