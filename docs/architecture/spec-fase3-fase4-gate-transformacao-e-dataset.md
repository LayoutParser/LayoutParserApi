# Spec executável — Fase 3 (gate de transformação) e Fase 4 (rótulo do dataset)

> **Autor:** @lp-architect (Aria) · **Status:** Pronta para execução · **Data:** 2026-08-03
> **Executor:** @lp-backend-dev (Dex) · **Branch:** `fix/parse-idoc-gate`
> **Decisão de base:** [`adr-001-discriminador-formato-posicional.md`](adr-001-discriminador-formato-posicional.md)
> **Natureza:** especificação — o objetivo é que o Dex não precise re-derivar nada.

---

## 0. Sequenciamento e bloqueio

```
Fase 1 (Dex, em andamento)  guard de !result.Success  →  422           [independente]
Fase 2 (Lia, em andamento)  WithBreakLines canônico   →  PositionalFormat
                                     │
                                     ▼  BLOQUEIA
Fase 3 (Dex, esta spec)     allowlist do gate         →  IDOC entra na transformação
Fase 4 (Dex, esta spec)     rótulo no meta.json       →  dataset separa os 2 formatos
```

> 🔴 **Não mergear a Fase 3 antes da Fase 2 estar validada.** Abrir o gate com o `LineSplitter` ainda
> quebrado troca "XML vazio" por "XML preenchido com dado fiscal errado" — e passa a gravar esses pares
> input→output corrompidos no dataset de aprendizado. Trocar uma falha visível por uma silenciosa é
> regressão, não progresso.

**Dependência de contrato da Fase 2 → 3/4:** a Fase 3 precisa que a Fase 2 exponha, a partir do layout
já carregado, algo com esta forma semântica (nome/local a critério da Lia):

| Item | Valores | Uso na Fase 3/4 |
|---|---|---|
| `Format` | `RecordPerLine` \| `ContinuousStream` | rótulo do dataset (Fase 4) |
| `Source` | `Layout` \| `Heuristic` \| `Default` | qualidade do rótulo (Fase 4) |
| `WithBreakLinesRaw` | `true` \| `false` \| `null` (ausente) | rastreabilidade (Fase 4) |

Se a Fase 2 entregar isso com outros nomes, **use os nomes dela** — o que importa é o tri-estado de
§4.2 da ADR chegar íntegro ao `meta.json`.

---

## 1. Fase 3 — allowlist de tipos posicionais

### 1.1 O que muda

`Controllers/ParseController.cs:145-147`. Hoje:

```csharp
if (!string.IsNullOrWhiteSpace(flattenedLayout.LayoutGuid) &&
    !string.IsNullOrWhiteSpace(result.RawText) &&
    detectedType == "mqseries")
```

As **duas primeiras condições permanecem exatamente como estão** — são invariantes reais do pathway
(sem `LayoutGuid` não há como selecionar mapper; sem `RawText` não há o que transformar). Só a terceira
é substituída por uma allowlist nomeada e comentada.

### 1.2 A allowlist exata

| `detectedType` | Entra? | Justificativa |
|---|---|---|
| `"mqseries"` | ✅ | comportamento atual — **não pode mudar em nada** |
| `"idoc"` | ✅ | **o objetivo desta fase**; o serviço já é agnóstico de tipo (§1.4) |
| `"unknown"` | ✅ | ver §1.3 — inclusão defensiva, não é o caso do IDOC Marelli |
| `"txt"` | ✅ | reservado: `LayoutDetector` não emite hoje, mas `SaveFileForLearningAsync` usa esse rótulo (`ParseController.cs:253-258`) e um detector futuro pode emitir |
| `"xml"` | ❌ | ver §1.5 |

**Forma recomendada:** `private static readonly HashSet<string> PositionalTypes` com
`StringComparer.OrdinalIgnoreCase`, declarado no topo da classe com comentário PT-BR apontando para esta
spec. Preferir isso a `!= "xml"`: a allowlist deixa a decisão auditável e evita que um tipo futuro
(ex.: `"edifact"`, `"json"`) caia no pathway por omissão.

**Não** mudar a comparação para depender do `PositionalFormat` da Fase 2 aqui. São perguntas diferentes:
o gate pergunta *"este documento é candidato a transformação?"*; o `PositionalFormat` responde *"como
fatiar as linhas?"*. Acoplar as duas reintroduz a confusão que a ADR-001 desfaz.

### 1.3 Por que `"unknown"` entra

**Primeiro, o fato verificado sobre o caso concreto:** o IDOC real da Marelli
(`.claude/tmp/servidor/layoutparser/Examples/LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe/`, 8 amostras)
começa com `EDI_DC40  6100000000194026465750 3012X ZRSDM_NFE_400…`. Logo:
`LooksLikeMqSeries` falha (não começa com `HEADER`, `LayoutDetector.cs:69-70`) e `LooksLikeIdoc`
casa no `StartsWith("EDI_")` (`LayoutDetector.cs:104`) → `detectedType = "idoc"`.
**Confirmado: o gate `== "mqseries"` (`ParseController.cs:147`) é a causa direta do XML de
transformação vazio para este documento.** Não é serialização, não é o front, não é falta de mapper.

`"unknown"` entra por **precaução**, não por causa deste caso. `LayoutDetector.DetectType` só devolve
`"xml" | "mqseries" | "idoc" | "unknown"` (`LayoutDetector.cs:13,20,26,32,36`), e a detecção de IDOC
depende de a **primeira linha** começar com `EDI_` / conter `ZRSDM_`, ou de uma heurística de tokens
(`LayoutDetector.cs:99-108`). Um IDOC de outro cliente que não case nisso vira `"unknown"` — e um gate
que exclua `unknown` deixaria o mesmo bug de pé para o próximo cliente.

O risco de admitir `unknown` é baixo e limitado:

- o arquivo já passou pelo early-return de XML (`ParseController.cs:85-96`);
- o layout foi **escolhido deliberadamente** pelo usuário no front (`UploadSection.tsx:91-95`);
- o parse do layout **sucedeu** (com a Fase 1, `Success=false` retorna 422 antes de chegar aqui);
- sem mapper cadastrado, o custo é **uma consulta ao banco** e `Applicable=false` (§1.6);
- o teto síncrono de `LowCode:SyncDeliveryTimeoutSeconds` (`ParseController.cs:149`, default 6s) já
  limita o pior caso de latência.

### 1.4 Por que isto é seguro para MQSeries

`LowCodeAutoTransformationService` **não usa `detectedType` para nenhuma decisão** — só o grava como
metadado (`LowCodeAutoTransformationService.cs:184` e `:305`). A seleção de candidatos é por
`layoutGuid` via `GetRankedMapperCandidatesForLayoutGuidAsync` (`:80-83`). Ou seja: para um documento
MQSeries, o caminho executado depois do gate é **bit a bit o mesmo** de hoje. Isso é o que torna a
Fase 3 barata — e é também o que o teste de controle do Quinn precisa provar.

### 1.5 Por que `xml` fica de fora

Dois motivos independentes, e vale manter os dois:

1. **Já sai antes.** `ParseController.cs:85-96` retorna cedo para `isXmlFile || detectedType == "xml"`,
   com `fileType="xml"` e instrução de processar no front (`xmltools.js`). Na prática `"xml"` nunca
   chega à linha 147.
2. **Defesa em profundidade.** Se aquele early-return mudar um dia, a allowlist impede que XML caia no
   pathway low-code silenciosamente. O runner sysmiddle espera texto posicional — a mesma premissa está
   documentada em `TransformationExecutionController.cs:241-244`.

Manter a exclusão explícita custa uma linha e remove uma classe inteira de regressão futura.

### 1.6 Comportamento quando não há mapper para o `layoutGuid`

Fluxo atual, **que não muda**: `GetRankedMapperCandidatesForLayoutGuidAsync` volta vazio →
`LowCodeAutoTransformationService.cs:85-89` loga
`"Nenhum mapper encontrado para layoutGuid={LayoutGuid} nos pacotes permitidos"` e devolve
`Applicable = false` → `ParseController.cs:165` mantém `transformationsStatus = "not_applicable"`.

**Problema de observabilidade a corrigir nesta fase:** `"not_applicable"` hoje significa três coisas
distintas — (a) tipo fora do gate, (b) sem mapper cadastrado, (c) `LayoutGuid`/`RawText` vazios. Foi
justamente essa ambiguidade que tornou o bug original difícil de diagnosticar: o front mostrava vazio e
não havia como saber por quê sem ler o log do servidor.

| Opção | Prós | Contras |
|---|---|---|
| **A** — novo valor `"no_mapper"` em `transformationsStatus` | mais direto de ler | **breaking**: `LayoutParserReact/src/types/api.ts:77` tipa a união fechada `'completed' \| 'processing' \| 'not_applicable' \| 'error'` — exige mudança coordenada no front |
| **B** — manter o status e adicionar `transformationsReason` (string, opcional) ✅ | **aditivo**: front atual ignora o campo desconhecido sem quebrar; o front pode adotar depois, no seu ritmo | mais um campo no payload |

**Escolher B.** Valores sugeridos: `"type_not_positional"`, `"no_mapper"`, `"empty_input"`,
`"timeout_sync"`, `"structural_error"`. Preencher junto de cada atribuição de `transformationsStatus`
(`ParseController.cs:141-142`, `:165`, `:174`, `:189`). Sem mudança no front nesta fase — abrir tarefa
separada para o consumo, e avisar `@lp-doc` para o Swagger.

### 1.7 Fora de escopo da Fase 3

- Não tocar em `LineSplitter.cs` nem na regra de offset — Fase 2, da Lia.
- Não mudar `LowCode:SyncDeliveryTimeoutSeconds` nem a lógica de fallback para background
  (`ParseController.cs:158-180`). Se o IDOC estourar o teto com frequência, isso é **medição** para
  depois, não ajuste preventivo agora.
- Não mexer no early-return de XML.

---

## 2. Fase 4 — rótulo de formato no dataset

### 2.1 O problema que esta fase resolve

Com a Fase 3, o IDOC passa a gerar o trio `input.txt` + `lowcode.xml` + `meta.json` no store
(`ML:LowCodeTransformationsPath`, `LowCodeAutoTransformationService.cs:35-37`). Sem rótulo de formato,
o dataset passa a **misturar dois formatos físicos incompatíveis** sob o mesmo guarda-chuva — repetindo,
na camada de dados, exatamente o erro que `LayoutType = "TextPositional"` cometeu na camada de código.
Um RAG que recupere exemplos por similaridade textual passaria a sugerir regra de MQ para IDOC.

E há um agravante já no código: `TransformationExecutionController.cs:258` chama `RunAsync` com
`detectedType: "mqseries"` **hardcoded**, independentemente do documento real. Toda amostra gerada por
esse caminho entra no dataset com **rótulo falso** — inclusive IDOCs. Corrigir isso é parte da Fase 4
(§2.4), não um item separado.

### 2.2 Campos a adicionar no `meta.json`

**Dois pontos, mesmo conjunto de campos:** `LowCodeAutoTransformationService.cs:179-193` (caminho
`N==1`) e `:300-313` (caminho multi-candidato). Os campos novos ficam no **objeto raiz** do meta nos
dois casos (são propriedades do *input*, não do candidato) — assim um consumidor lê o rótulo sem
precisar saber se a amostra é single ou multi.

| Campo | Tipo | Valores | Por quê |
|---|---|---|---|
| `positionalFormat` | string | `"RecordPerLine"` \| `"ContinuousStream"` | **o rótulo principal** — separa os dois formatos no dataset |
| `positionalFormatSource` | string | `"layout"` \| `"heuristic"` \| `"default"` | qualidade/confiança do rótulo. Amostra `"default"` é palpite, não fato — treinar preferindo `"layout"` |
| `withBreakLines` | bool? | `true` \| `false` \| `null` | valor bruto do layout, `null` = elemento ausente. Rastreabilidade: permite reprocessar o rótulo se a regra mudar |
| `layoutType` | string | ex.: `"TextPositional"` | contexto; deixa explícito no dado o quanto `LayoutType` é ambíguo |
| `datasetSchemaVersion` | int | `2` | **crítico** — ver §2.3 |
| `suspect` | bool | `true` \| `false` | quarentena — ver §2.3 |
| `suspectReason` | string? | ex.: `"pre-adr-001-positional-split"` | por que a amostra é suspeita; `null` quando `suspect=false` |

Os campos atuais (`createdAtUtc`, `layoutGuid`, `layoutName`, `detectedType`, `originalFileName`,
`sha256`, `inputLength`, …) **permanecem inalterados**. `detectedType` continua sendo o palpite do
detector; `positionalFormat` é a verdade derivada do layout — são coisas diferentes e o dataset ganha em
ter as duas (a divergência entre elas é sinal de detector fraco, útil para a Lia).

### 2.3 `datasetSchemaVersion` e quarentena

- **`datasetSchemaVersion = 2`** para tudo gravado a partir desta fase. Amostras sem o campo são
  implicitamente v1 = **anteriores à ADR-001**, portanto de formato desconhecido e potencialmente
  produzidas pelo split corrompido. Sem esse marcador, não há como um consumidor futuro separar o joio
  do trigo — e o custo de reconstruir isso depois é alto.
- **`suspect`**: `true` quando a amostra foi gerada em condição de risco conhecido. Regra concreta e
  suficiente para esta fase: `suspect = (positionalFormatSource != "layout")` — rótulo inferido, não
  afirmado pelo contrato.
- A **política de quarentena das amostras já persistidas** (v1) é entrega da **Lia**, na Fase 2. Esta
  spec só garante que as amostras **novas** carreguem o marcador. Dex não precisa mexer em arquivo
  histórico.

### 2.4 Como o dado chega ao serviço (mudança de assinatura)

`LowCodeAutoTransformationService` não conhece o `Layout` — recebe só `detectedType` (string). Precisa
receber o formato resolvido de quem tem o layout em mãos.

**Recomendado:** um `record` pequeno (`PositionalFormatInfo` ou o nome que a Fase 2 usar) em vez de
mais três parâmetros soltos — `RunAsync` já tem 5. Passar como parâmetro **opcional** (`= null`) mantém
compatibilidade e permite migrar os call-sites um a um.

Assinaturas afetadas: `LowCodeAutoTransformationService.cs:40` (`RunInBackgroundAsync`), `:65`
(`RunAsync`), `:68` (`TransformAndPersistAsync`), `:153` e `:215` (os dois persistidores).

**Call-sites — são só dois** (verificado nesta sessão; `RunInBackgroundAsync` não tem nenhum chamador
hoje):

1. `ParseController.cs:174` — passar o formato resolvido do `flattenedLayout`.
   ⚠️ **`flattenedLayout` é construído em `ParseController.cs:112-120` sem copiar `WithBreakLines`** —
   se a resolução for feita a partir dele, **copiar o campo junto** (é o 3º elo da cadeia de perda da
   ADR-001 §4.1; os outros dois são da Lia). Alternativa mais segura: resolver a partir de
   `result.Layout` (antes das cópias) e passar adiante o resultado.
2. `TransformationExecutionController.cs:254-259` — **remover o `"mqseries"` hardcoded** e passar o
   formato real do `layoutRecord`. Se esse caminho não tiver o layout completo carregado, passar `null`
   e deixar `positionalFormatSource = "default"` + `suspect = true` — **honesto é melhor que
   confiante e errado**. O que não pode continuar é afirmar `"mqseries"` para todo mundo.

### 2.5 Fora de escopo da Fase 4

- Migrar/reprocessar amostras v1 (Lia).
- Mudar o layout de diretórios do store (`{storePath}/{yyyyMMdd}/{sha}_{HHmmss}.*`) — estável, não
  mexer.
- Consumir o novo rótulo no RAG — trabalho da Lia, depois que houver volume de amostras v2.

---

## 3. Ordem de commits sugerida

1. `feat: allowlist de tipos posicionais no gate de transformação` — §1.2 + §1.6 (`transformationsReason`).
2. `feat: rotula formato posicional no meta.json do dataset low-code` — §2.2 a §2.4.
3. `fix: remove detectedType hardcoded no pathway sysmiddle` — §2.4 item 2, se ficar grande demais para
   o commit 2.

Quality gate entre cada um: `dotnet build` limpo. Não concluir com build quebrado
(`.claude/rules/dotnet-standards.md`).

---

## 4. Critérios de aceite — @lp-qa (Quinn)

Checklist verificável das Fases 1 a 4. **Veredito global só é PASS com todos os itens obrigatórios
verdes** — um item vermelho volta ao dev responsável com o item citado.

### 4.0 Pré-condições (verificar ANTES de rodar; item não atendido = BLOCKED, não FAIL)

| # | Pré-condição | Estado verificado em 2026-08-03 |
|---|---|---|
| P1 | Documento IDOC real da Marelli | ✅ **disponível** — `.claude/tmp/servidor/layoutparser/Examples/LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe/` (8 arquivos de 7377 bytes, primeira linha `EDI_DC40…`) |
| P2 | Documento MQSeries de controle | ✅ **disponível** — `.claude/tmp/servidor/layoutparser/Examples/LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe/` |
| P3 | **XML do layout** `LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe` | 🔴 **NÃO está no repo** — vem do banco/Redis via front (`UploadSection.tsx:63-89`). Exige API de dev com DB/Redis acessíveis. **Não substituir por layout fabricado:** a Fase 2 depende do `<WithBreakLines>` **real** desse XML |
| P4 | Existe mapper cadastrado para o `layoutGuid` da Marelli, no `ProjectId`/`AllowedPackageGuids` configurados | ⚠️ **provável, não confirmado** — existe `xsl/MAP_MARELLI_SAP_SEND_ENV_TXT_XML_NFE_LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe.xsl` no dump do servidor, mas isso não prova a linha no banco. **Confirmar antes de rodar 4.3/4.4**: sem mapper, `Applicable=false` e o resultado é vazio por outro motivo (`LowCodeAutoTransformationService.cs:85-89`) |
| P5 | Log do request original que retornou 500 | 🔴 **não disponível** — seria a confirmação direta da Causa A. `ParseController.cs:210` loga a exceção completa antes de mascarar. Se recuperável no servidor de dev, anexar ao relatório; **não bloqueia** o teste |
| P6 | Valor esperado de `<WithBreakLines>` no layout Marelli | ⚠️ **a confirmar em P3** — a expectativa é `true` (IDOC = `RecordPerLine`). Se vier `false`/ausente, isso **não é falha do teste**: é achado que aciona §5.3 da ADR-001 (validação cruzada) e muda o caminho esperado para o fallback heurístico |

### 4.1 Fase 1 — o 422 (Dex) · OBRIGATÓRIO

- [ ] **1.1** Upload com layout XML **não parseável** (ex.: XML truncado/malformado) → HTTP **422**, não 500.
- [ ] **1.2** O corpo do 422 contém a **mensagem real** de `result.ErrorMessage` (padrão `"Erro no parsing: …"`, `LayoutParserService .cs:171`) — **não** `"Object reference not set to an instance of an object"`.
- [ ] **1.3** Nenhum `NullReferenceException` no log para esse request (o NRE de `ParseController.cs:114` deixou de ser alcançável).
- [ ] **1.4** Front: com o 422, a tela mostra **erro explícito**, distinguível do estado "ainda não processei" (`UploadSection.tsx:131-134` popula `uploadError`).
- [ ] **1.5** **Não-regressão:** upload válido continua **200** com o mesmo shape de resposta de hoje.

### 4.2 Fase 2 — split correto do IDOC (Lia) · OBRIGATÓRIO — regressão que originou tudo

Documento: P1. Layout: P3.

- [ ] **2.1** `CUF == '35'` (hoje sai `'47'`) — 🔴 **gate principal**.
- [ ] **2.2** `MOD == '55'` (hoje sai `'00'`) — 🔴 **gate principal**.
- [ ] **2.3** As 55 linhas continuam sendo identificadas (não regredir a contagem para "corrigir" o valor).
- [ ] **2.4** Amostragem de **pelo menos mais 5 campos** de segmentos distintos conferidos contra o documento fonte — 2.1/2.2 sozinhos não provam que o offset foi corrigido, só que dois campos casaram.
- [ ] **2.5** Log registra o formato resolvido e a **origem** (`layout` / `heuristic` / `default`) para o layout Marelli.
- [ ] **2.6** Se a origem for `heuristic` ou `default`, há **Warning** no log com o nome do layout (ADR-001 §5.1).

### 4.3 Fase 2/3 — controle MQSeries byte-a-byte · OBRIGATÓRIO — o risco mais alto da mudança

Documento: P2 (e, se possível, um segundo layout MQ distinto).

- [ ] **3.1** Capturar o XML de saída low-code **antes** das mudanças (baseline no `master`/branch anterior).
- [ ] **3.2** XML de saída **depois** das mudanças é **byte-a-byte idêntico** ao baseline. Qualquer diff — inclusive whitespace — é **FAIL** até ser explicado e aceito por `@lp-architect`.
- [ ] **3.3** `detectedType` continua `"mqseries"` e `transformationsStatus` continua `"completed"`.
- [ ] **3.4** Campos parseados (`fields`) idênticos ao baseline em quantidade e valor.
- [ ] **3.5** `lineValidations` / `validationErrors` inalterados para o mesmo documento.

### 4.4 Fase 3 — IDOC entra no pathway de transformação · OBRIGATÓRIO

Depende de P3 e **P4**.

- [ ] **4.1** Upload do IDOC Marelli → `transformationsStatus == "completed"` (era `"not_applicable"`).
- [ ] **4.2** `transformations` vem **não-vazio**, com pelo menos um candidato `success: true` e `outputXml` não-vazio.
- [ ] **4.3** ⚠️ **Verificar no response, não na tela.** Corrigido em 2026-08-03: o front **não consome** `transformations` — `ParseResponse` (`LayoutParserReact/src/types/api.ts:55-78`) sequer declara o campo, e a aba "XML Transformação Final" é alimentada por um botão que chama `POST /api/transformation-execution/execute-candidates` (`XmlTransformationDisplay.tsx:118`). Validar 4.1/4.2 via response HTTP (Swagger/curl/Cypress de API), **não** pela UI. Ver `handoff-frontend-fases-1-4-idoc.md`.
- [ ] **4.4** O XML gerado é **coerente com 4.2 da Fase 2** — contém `<CUF>35`/`<mod>55` (ou equivalente no schema alvo). XML preenchido com valor errado é **FAIL**, não PASS parcial: é o modo de falha que esta correção existe para eliminar.
- [ ] **4.5** Sem mapper cadastrado (cenário negativo, se reproduzível): resposta 200, `transformationsStatus == "not_applicable"`, `transformationsReason == "no_mapper"` e o Warning `"Nenhum mapper encontrado para layoutGuid=…"` no log.
- [ ] **4.6** Arquivo `.xml` continua caindo no early-return (`fileType: "xml"`), sem tocar o pathway low-code.

### 4.5 Fase 4 — o trio no store low-code · OBRIGATÓRIO

Store: `ML:LowCodeTransformationsPath` → `{storePath}/{yyyyMMdd}/`.

- [ ] **5.1** Após 4.1, existem os três arquivos com o mesmo `baseName` (`{sha256}_{HHmmss}`): `.input.txt`, `.lowcode.xml`, `.meta.json`.
- [ ] **5.2** `.input.txt` é **idêntico** ao IDOC enviado.
- [ ] **5.3** `.lowcode.xml` é idêntico ao `outputXml` devolvido no response.
- [ ] **5.4** `.meta.json` contém `positionalFormat == "RecordPerLine"`.
- [ ] **5.5** `.meta.json` contém `positionalFormatSource`, `withBreakLines`, `layoutType`, `datasetSchemaVersion == 2`, `suspect`, `suspectReason`.
- [ ] **5.6** `suspect == (positionalFormatSource != "layout")` — coerência da regra da spec §2.3.
- [ ] **5.7** Amostra de **MQSeries** gerada no mesmo dia traz `positionalFormat == "ContinuousStream"` — prova que o dataset **separa** os dois formatos.
- [ ] **5.8** Caminho multi-candidato (se reproduzível, N>1 mapeadores): os mesmos campos aparecem no **objeto raiz** do meta, não só por candidato.
- [ ] **5.9** `TransformationExecutionController` não grava mais `detectedType: "mqseries"` para entrada não-MQ (era hardcoded em `:258`).

### 4.6 Quality gates gerais · OBRIGATÓRIO

- [ ] **6.1** `dotnet build` sem erros.
- [ ] **6.2** `dotnet test` verde (incluindo `LayoutParserApi.Tests`).
- [ ] **6.3** Nenhum segredo novo no diff (`appsettings.json` intocado).
- [ ] **6.4** Testes novos cobrindo: split `RecordPerLine` vs `ContinuousStream`, a allowlist do gate (um caso por tipo) e a serialização dos campos novos do meta.

### 4.7 Desejável (não bloqueia o merge)

- [ ] **7.1** Medir a latência do IDOC no pathway low-code contra `LowCode:SyncDeliveryTimeoutSeconds` (default 6s) — se estourar com frequência, reportar como **dado**, não ajustar o teto por precaução (spec §1.7).
- [ ] **7.2** Verificar quantos layouts do catálogo caem no fallback por ausência de `<WithBreakLines>` (insumo do inventário da ADR-001 §5.2).
