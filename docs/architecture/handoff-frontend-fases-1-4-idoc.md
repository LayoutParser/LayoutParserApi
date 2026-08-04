# Handoff → @lp-front-dev — os três estados vazios do parse (Fases 1–4, correção IDOC)

> **Autor:** @lp-architect (Aria) · **Data:** 2026-08-03 · **Destinatário:** `@lp-front-dev` (repo `LayoutParserReact`)
> **Origem:** branch `fix/parse-idoc-gate` do `LayoutParserApi` · commit da Fase 1: `7f54e28`
> **Base de decisão:** [`adr-001-discriminador-formato-posicional.md`](adr-001-discriminador-formato-posicional.md) ·
> [`spec-fase3-fase4-gate-transformacao-e-dataset.md`](spec-fase3-fase4-gate-transformacao-e-dataset.md)
> **Nota de execução:** este documento foi escrito **sem escrever nada** no repo do front (que está em
> `feat/design-tokens-padronizacao-visual` com trabalho não commitado). Todas as referências a arquivo:linha
> do front são de **leitura**, verificadas em 2026-08-03.

---

## 1. Artefato de handoff

```yaml
handoff:
  from_agent: "@lp-architect (Aria)"
  to_agent: "@lp-front-dev"
  contexto:
    tarefa: "Front distinguir os 3 estados que hoje colapsam em 'tela vazia': erro de parse (novo 422), parse OK sem transformação, e transformação degradada para background"
    branch: "LayoutParserApi: fix/parse-idoc-gate | LayoutParserReact: feat/design-tokens-padronizacao-visual (com trabalho não commitado)"
    arquivos_tocados:
      - "LayoutParserReact/src/components/upload/UploadSection.tsx (a alterar)"
      - "LayoutParserReact/src/services/api.ts (a alterar)"
      - "LayoutParserReact/src/types/api.ts (a alterar)"
      - "LayoutParserReact/src/store/useAppStore.ts (a alterar)"
      - "LayoutParserReact/src/components/analysis/AnalysisModeTabs.tsx (a alterar)"
      - "LayoutParserApi/Controllers/ParseController.cs:109-131 (já alterado pelo Dex — só leitura)"
  decisoes:
    - "Backend passou a devolver 422 com body JSON {success,detectedType,message} quando o layout não é parseável, em vez de 500 opaco"
    - "O front NÃO consome parseResult.transformations — a aba de XML é alimentada por chamada própria a execute-candidates. Isso muda o que a Fase 3 entrega na tela: nada, sem trabalho de front"
    - "'not_applicable' hoje significa 3 coisas distintas; backend vai passar a mandar transformationsReason (aditivo, opcional)"
    - "Não existe polling/SSE em lugar nenhum do front, nem endpoint de leitura do resultado em background no backend"
  bloqueios:
    - "BUG-CONTRATO: transformationsStatus='processing' nunca resolve — o rótulo '(processando...)' fica preso para sempre. Precisa de decisão (§5)"
    - "Repo do front tem trabalho não commitado na branch de design tokens — commitar/stashar antes de começar"
    - "Não temos o payload real de um 422 capturado em execução (só o contrato declarado pelo Dex)"
  proximo_passo: "Implementar §3 (422) primeiro — é o que devolve diagnóstico ao usuário. §4 e §5 podem vir depois, na ordem."
```

---

## 2. O que mudou no backend (e o que NÃO mudou)

**Fase 1 — já commitada** (`ParseController.cs:109-131`, commit `7f54e28`): guard entre `ParseAsync` e
`ReestruturarLayout`. Quando `!result.Success || result.Layout == null`, o endpoint devolve
**HTTP 422 Unprocessable Entity**:

```json
{ "success": false, "detectedType": "idoc", "message": "Erro no parsing: <mensagem real da exceção>" }
```

- Os **três campos sempre vêm**.
- `detectedType` já passou pelos overrides de extensão/`layoutName` (`ParseController.cs:74-82`).
- O prefixo `"Erro no parsing: "` **faz parte** da `message` — não é campo separado.
- Header `X-Correlation-ID` presente (o front já o gera em `api.ts:63-70`).

**Antes disso**, o mesmo cenário virava `500` com body **string** (`"Erro interno: Object reference not
set to an instance of an object."`) — mensagem inútil, porque a mensagem real era destruída por um
`NullReferenceException` mais adiante.

**Fases 3 e 4** (ainda não mergeadas) mudam o comportamento do IDOC no backend e no dataset —
**impacto no front: ver §6 (spoiler: quase nenhum).**

---

## 3. Estado 1 — erro de parse (422)

### 3.1 Onde está hoje

`UploadSection.tsx:131-134`:

```tsx
} catch (error) {
  const errorMessage = error instanceof Error ? error.message : 'Erro desconhecido';
  setUploadError(errorMessage);
  console.error('❌ Erro no parsing:', error);
}
```

Qualquer não-2xx cai aqui: 422, 500, timeout, rede, CORS. `parseResult` continua `null` →
`AnalysisModeTabs` retorna `null` (`AnalysisModeTabs.tsx:82-84`) → **a tela mostra só os botões**.

### 3.2 Fato que reduz o tamanho do trabalho

`parseService.parseFiles` **já extrai a mensagem do body** (`api.ts:99-108`):

```ts
throw new Error(error.response?.data?.message || error.message || 'Erro ao processar arquivos');
```

Ou seja: **assim que o backend passa a mandar `{message}` em JSON, o texto correto já chega no
`uploadError` sem nenhuma mudança de front.** Com o 500 antigo o body era string pura, `data.message`
era `undefined` e o usuário via `"Request failed with status code 500"`.

O que ainda se perde no caminho: o **status HTTP** (não dá pra distinguir 422 de 500 de rede), o
**`detectedType`**, e o **`correlationId`** (útil para o usuário reportar). É isso que a mudança abaixo
recupera.

### 3.3 Mudança recomendada

**a) Tipo do erro** — `src/types/api.ts`, ao lado de `ParseResponse`:

```ts
export type ParseErrorKind = 'parse_error' | 'server_error' | 'network_error';

export interface ParseErrorInfo {
  kind: ParseErrorKind;
  message: string;              // mensagem já pronta para exibir (vem do body no 422)
  httpStatus?: number;          // 422 | 500 | undefined (rede/timeout)
  detectedType?: string;        // só no 422
  correlationId?: string;       // header X-Correlation-ID da resposta
}
```

**b) Classificação** — `src/services/api.ts`, no `catch` de `parseFiles` (`:99-108`). Trocar o
`throw new Error(...)` por um erro que carregue a estrutura. Duas formas, escolha a que combinar com o
padrão do resto do repo:

- classe `ParseRequestError extends Error` com os campos de `ParseErrorInfo`; ou
- lançar o objeto e tipar o `catch` no chamador.

Regra de classificação:

| Condição | `kind` |
|---|---|
| `error.response?.status === 422` | `parse_error` |
| `error.response` existe, status ≥ 500 | `server_error` |
| `error.response` ausente (rede/timeout/CORS) | `network_error` |

`correlationId` sai de `error.response?.headers?.['x-correlation-id']` (axios normaliza os headers de
resposta em minúsculas).

**c) Estado no store** — `useAppStore.ts:9,33,45` tem `uploadError: string | null`. Duas opções:

| Opção | Prós | Contras |
|---|---|---|
| **A** — trocar para `ParseErrorInfo \| null` | um estado só, sem duplicação | quebra os outros `setUploadError('...')` com string literal (`UploadSection.tsx:36,41,57`) — todos precisam virar objeto |
| **B** — manter `uploadError: string` e **adicionar** `parseError: ParseErrorInfo \| null` ✅ | aditivo, não mexe nos usos de validação de formulário (que são erros de UI, não de API); migração incremental | dois estados de erro convivendo — exige limpar os dois no início do submit |

**Recomendo B.** Os `setUploadError` das linhas 36/41/57 são mensagens de validação local
("selecione um layout", "Buscando layout completo da API...") — natureza diferente de um erro de API, e
misturar as duas coisas num tipo só é o que costuma gerar bug de estado preso.

**d) Renderização** — onde `uploadError` é exibido hoje, tratar `parseError.kind`:

- `parse_error` (422): **não é erro de sistema, é diagnóstico do documento**. Mostrar a `message` como
  texto principal (já vem em PT-BR e já explica a causa) + `detectedType` como contexto secundário
  ("tipo detectado: idoc"). Tom de "documento/layout inválido", não de "falha da aplicação".
- `server_error` (5xx): tom de falha de sistema + `correlationId` visível para o usuário reportar.
- `network_error`: tom de conectividade + sugestão de tentar de novo. Não mostrar `message` cru do
  axios ("Request failed with status code…") como texto principal.

> **Acessibilidade:** o bloco de erro deve ter `role="alert"` — é o padrão já usado em
> `XmlTransformationDisplay.tsx:202` e `AnalysisModeTabs.tsx:127`.

### 3.4 Critério de aceite

- Layout XML corrompido → mensagem real do backend na tela (`"Erro no parsing: …"`), **nunca**
  `"Request failed with status code 500"` nem `"Object reference not set…"`.
- 422, 500 e queda de rede produzem **três apresentações visualmente distintas**.
- Upload válido continua funcionando igual (não-regressão).

---

## 4. Estado 2 — parse OK, sem transformação (`not_applicable`)

### 4.1 O que o front faz hoje

- `AnalysisModeTabs.tsx:106-107` lê `transformationsStatus` e só o usa para o rótulo da aba.
- `AnalysisModeTabs.tsx:109` — a aba "XML Transformação Final" **só existe se `mapperAvailable`**, que
  vem de uma chamada separada (`checkMapperAvailability`, `:57-59`), **não** de `transformationsStatus`.
- `AnalysisModeTabs.tsx:126-130` mostra banner só para `'error'`. **`'not_applicable'` não produz
  nenhuma mensagem** — silêncio total.

Resultado: parse correto + `not_applicable` = documento aparece normalmente na aba TXT Posicional
(comportamento certo), mas o usuário não tem nenhuma pista de por que não há XML.

### 4.2 O que muda no backend (Fase 3, spec §1.6)

Novo campo **aditivo e opcional** no payload do parse — `transformationsStatus` **não muda de valores**
(a união em `types/api.ts:77` continua válida):

```ts
transformationsReason?: 'type_not_positional' | 'no_mapper' | 'empty_input' | 'timeout_sync' | 'structural_error';
```

### 4.3 Mudança recomendada

1. Declarar `transformationsReason` em `ParseResponse` (`types/api.ts:55-78`), como **opcional** — o
   backend pode ainda não estar mandando, e nesse caso o front cai no texto genérico.
2. Em `AnalysisModeTabs.tsx`, estender o bloco de banner (`:126-130`) para `'not_applicable'`, com texto
   por motivo:

| `transformationsReason` | Texto sugerido |
|---|---|
| `no_mapper` | "Nenhum mapeador de transformação cadastrado para este layout." |
| `type_not_positional` | "O tipo detectado deste documento não entra no pathway de transformação." |
| `empty_input` | "Documento sem conteúdo para transformar." |
| ausente/desconhecido | "Não há transformação XML disponível para este documento." |

Tom: **informativo (`role="status"`), não alarme.** Não é erro — é ausência esperada.

3. Se `mapperAvailable === false`, a aba nem existe; o banner acima cobre o caso, mas vale considerar
   mostrá-lo próximo às abas para o usuário entender a ausência da aba em si.

> ⚠️ **Não fazer:** derivar a existência da aba de `transformationsStatus`. O critério de negócio da aba
> é `mapperAvailable` — está documentado em `AnalysisModeTabs.tsx:13-20` e foi confirmado com o usuário.

---

## 5. Estado 3 — transformação assíncrona: 🔴 BURACO DE CONTRATO

### 5.1 O que foi verificado (não é suposição)

| Verificação | Resultado |
|---|---|
| Polling / `setInterval` / `EventSource` no front para transformação | 🔴 **não existe** — grep em `src/` só acha ocorrências em monitoring/aiMetrics/log. O comentário em `types/api.ts:76` diz literalmente "sem precisar de polling manual" |
| Endpoint no backend que leia o resultado persistido em background | 🔴 **não existe** — o store `ML:LowCodeTransformationsPath` é **write-only**: o único código que toca `_storePath` é o próprio `LowCodeAutoTransformationService` (`:35-36`, `:174`, `:283`). Nenhum controller lê |
| O front consome `parseResult.transformations` | 🔴 **não** — o campo **nem está declarado** em `ParseResponse` (`types/api.ts:55-78`) |

**Conclusão:** quando o low-code estoura `LowCode:SyncDeliveryTimeoutSeconds` (`ParseController.cs:149`,
default 6s), a resposta volta com `transformationsStatus: 'processing'` e **esse estado nunca resolve**.
O rótulo `'XML Transformação Final (processando...)'` (`AnalysisModeTabs.tsx:112-114`) fica preso
**para sempre** — nada re-consulta, e o resultado, embora persistido em disco, é **inalcançável por
qualquer cliente HTTP**.

### 5.2 O que atenua (e por que não é catastrófico hoje)

A aba de XML **não depende** dessa entrega. `XmlTransformationDisplay.tsx:106-147` tem um botão
"Gerar Transformação XML" que chama `POST /api/transformation-execution/execute-candidates`
**de forma síncrona e sob demanda**. Ou seja: o usuário consegue o XML clicando, independentemente do
que aconteceu no pathway do parse.

O dano real é: (a) rótulo mentiroso e permanente de "processando..."; (b) o trabalho já feito em
background é jogado fora e refeito no clique — desperdício de CPU no runner low-code, que é a parte
cara; (c) a promessa implícita de entrega assíncrona nunca foi cumprida.

### 5.3 Por que isso PIORA com a Fase 3

Hoje o IDOC nem entra no pathway (gate de `ParseController.cs:145-147`), então nunca vê `'processing'`.
Depois da Fase 3 ele entra — e documentos grandes vão estourar o teto de 6s com frequência. Aí o usuário
vê "processando..." eternamente. **Seria o terceiro motivo diferente para "XML vazio" na mesma tela**,
depois do 500 opaco e do gate. É exatamente o padrão que esta leva de correções existe para acabar.

### 5.4 Opções

| Opção | Escopo | Prós | Contras |
|---|---|---|---|
| **A — front-only: tornar `'processing'` acionável** ✅ **agora** | front | zero backend; elimina o rótulo preso; usa o caminho síncrono que já existe e já funciona | não reaproveita o trabalho do background |
| **B — backend expõe leitura do resultado + front faz polling** | backend + front | cumpre a promessa assíncrona; reaproveita o trabalho | novo endpoint, nova chave estável, polling no front |
| **C — SSE/WebSocket** | backend + front + infra | tempo real | desproporcional ao problema; nada no projeto usa hoje |

**Recomendação: A agora, B como dispatch para o Dex.** C fica fora.

**Opção A, concreto:** em `AnalysisModeTabs.tsx`, quando `transformationsStatus === 'processing'`,
trocar o rótulo permanente por um banner `role="status"`: *"A transformação deste documento continua
sendo processada em segundo plano. Abra a aba XML Transformação Final e clique em Gerar Transformação
XML para obtê-la agora."* Manter o `(processando...)` no rótulo apenas se houver um timer curto que o
remova — rótulo sem resolução é pior que rótulo nenhum.

**Opção B — o que o backend precisaria expor (para @lp-devops despachar ao Dex):**

1. No response do parse, quando `transformationsStatus == "processing"`, incluir
   `transformationsTicket` (string) — a chave estável do artefato. O `baseName` do store já é
   `{sha256}_{HHmmss}` sob `{storePath}/{yyyyMMdd}/` (`LowCodeAutoTransformationService.cs:171`,
   `:270`), então a chave existe; falta **devolvê-la**.
2. `GET /api/parse/transformations/{ticket}` → `200 { status: "completed", candidates: [...] }` |
   `{ status: "processing" }` | `404` se o ticket não existir/expirou.
3. Regras não-negociáveis: leitura **somente** dentro do `_storePath` com nome sanitizado (o ticket vem
   do cliente — **path traversal** é o risco óbvio); TTL/limpeza definidos; nunca vazar caminho absoluto
   de disco no payload.
4. Front: polling com backoff (ex.: 2s → 5s → 10s, teto ~60s) e cancelamento ao trocar de documento.

---

## 6. Impacto das Fases 3 e 4 no front

**Fase 3 (gate abre para IDOC):**

| Campo | Antes (IDOC) | Depois (IDOC) | Front precisa mudar? |
|---|---|---|---|
| `transformationsStatus` | sempre `"not_applicable"` | `"completed"` / `"processing"` / `"error"` | **Não** — a união de tipos já cobre os três |
| `transformations` (array) | `null` | array de candidatos | **Não** — o front não lê (e nem declara) |
| `transformationsReason` | inexistente | novo, opcional | **Sim, opcional** — §4.3 |
| `detectedType` | `"idoc"` | `"idoc"` (inalterado) | Não |
| `fields`, `documentStructure`, `text` | — | **valores corrigidos** pela Fase 2 (hoje 100% dos campos do IDOC saem errados) | Não — mesma forma, conteúdo certo |

> **O ponto que evita expectativa errada:** abrir o gate **não faz** o XML aparecer sozinho na aba. A
> aba é alimentada por `execute-candidates` sob demanda. A Fase 3 muda o **payload do parse** e o
> **dataset de aprendizado** — a tela só muda se o front passar a consumir `transformations` (o que é
> uma decisão à parte, não requisito desta leva).

**Fase 4 (rótulo do dataset):** zero impacto no front. Só acrescenta campos ao `meta.json` gravado em
disco no servidor.

---

## 7. Pré-condições e o que não temos

| # | Item | Estado |
|---|---|---|
| F1 | Payload real de um 422 capturado em execução | 🔴 **não temos** — o contrato de §2 é o **declarado** pelo Dex. Antes de codar o parser do erro, disparar um upload com XML corrompido contra a API de dev e **conferir o shape real** (principalmente se `data` chega como objeto, não string) |
| F2 | Repo do front está limpo | 🔴 **não** — `feat/design-tokens-padronizacao-visual` com `.env.*`, `ci-dev.yml`, `README.md` e memórias não commitados. **Commitar ou stashar antes de começar**, senão o diff desta tarefa fica ilegível |
| F3 | Backend com Fase 3 mergeada | ⚠️ ainda não — §4.3 (`transformationsReason`) pode ser implementado antes, com o campo opcional; só não dá para testar de ponta a ponta |
| F4 | Confirmação de que o 422 mantém `Content-Type: application/json` | ⚠️ presumido pelo uso de `Ok`/`UnprocessableEntity` com objeto anônimo. Verificar junto com F1 — se vier string, o `data?.message` de `api.ts:102` não pega nada |
| F5 | Frequência real de estouro do teto de 6s com IDOC | 🔴 **não medido** — determina a urgência da Opção B de §5.4. Medição é da @lp-qa (item 7.1 do checklist na spec das Fases 3/4) |

---

## 8. Ordem sugerida

1. **§3 (422)** — maior ganho, menor risco, independe das Fases 2/3. É o que devolve diagnóstico ao usuário.
2. **§4 (`not_applicable`)** — pode ir junto; campo opcional, degrada para texto genérico se o backend
   ainda não mandar.
3. **§5 Opção A (`processing` acionável)** — antes da Fase 3 chegar em produção, senão o rótulo preso
   vira o novo "XML vazio".
4. **§5 Opção B** — só depois de F5, e só se a medição justificar.
