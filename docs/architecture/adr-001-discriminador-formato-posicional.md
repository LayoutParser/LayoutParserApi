# ADR-001 — Discriminador canônico de formato posicional (`WithBreakLines`)

> **Autor:** @lp-architect (Aria) · **Status:** Aceita · **Data:** 2026-08-03
> **Branch de execução:** `fix/parse-idoc-gate`
> **Contexto de origem:** teste do Elson em 2026-08-03 (mapeador `LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe`,
> documento IDOC da Marelli) — tela do front vazia + XML de transformação final vazio.
> **Implementação:** @lp-parser-llm (Fase 2) · **Consome:** @lp-backend-dev (Fases 3/4) · **Valida:** @lp-qa
> **Relacionadas:** [`multi-client-layout-generalization.md`](multi-client-layout-generalization.md),
> [`ia-fiscal-diagnosis-vision.md`](ia-fiscal-diagnosis-vision.md)

---

## 1. Contexto e problema

`LayoutType = "TextPositional"` é um **tipo sobrecarregado**: cobre dois formatos físicos
mutuamente incompatíveis.

| Formato | Estrutura física | Campo `Sequencia` | Exemplo |
|---|---|---|---|
| **MQSeries** (`ContinuousStream`) | stream contínuo, sem quebra de linha; fatiado a cada N chars (legado 600) | **Sim** — 6 chars por registro | `LAY_*_MQ_*` |
| **IDOC SAP** (`RecordPerLine`) | um registro por linha física (LF), largura variável por segmento | **Não** | `LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe` |

O runtime não distingue os dois. `Services/Parsing/Implementations/LineSplitter.cs:23` trata
`layoutType == "mqseries" || layoutType == "TextPositional"` como largura fixa, e a regra de offset
aplica o salto de 6 chars do sequencial MQ a um formato que **não tem esse campo**.

### Modo de falha observado (o pior possível)

Teste de 2026-08-03 com o IDOC real da Marelli:

- **55/55 linhas identificadas**, `Success = true`, **zero erro reportado**;
- **100% dos campos com valor errado** — `CUF = '47'` em vez de `'35'`, `MOD = '00'` em vez de `'55'`.

Ou seja: **corrupção silenciosa de dado fiscal**. Não há alarme, não há validação que pegue, e o
resultado é indistinguível de um parse correto para quem olha só o status. Pior ainda no contexto de
IA: esses pares input→output alimentam o dataset de aprendizado low-code, envenenando o RAG.

### O discriminador correto já existe — e ninguém lê

O XML do layout **já carrega** `<WithBreakLines>`, e ele separa os dois formatos exatamente.
O mapeador modela o IDOC corretamente (validado em **136/139 segmentos**:
`len(InitialValue) + len(campo "content") == 63`, que é o header EDI_DD40 —
SEGNAM 30 + MANDT 3 + DOCNUM 16 + SEGNUM 6 + PSGNUM 6 + HLEVEL 2). **Quem erra é o runtime.**

Estado do campo no código, verificado nesta sessão:

| Ponto | Lê/propaga `WithBreakLines`? | Evidência |
|---|---|---|
| Entidade de domínio | ✅ existe | `Models/Entities/Layout.cs:22` |
| Loader do fluxo **Generation** | ✅ lê | `Services/Generation/Implementations/XmlLayoutLoader.cs:47` |
| Loader do fluxo **Parsing** (`ParseAsync`) | 🔴 **NUNCA lê** | `Services/Implementations/LayoutParserService .cs:1002-1010` |
| `LayoutNormalizer.ReestruturarLayout` | 🔴 **descarta** (cria `new Layout` sem o campo) | `Services/Parsing/Implementations/LayoutNormalizer.cs:15-23` |
| `ReordenarSequences` | ✅ muta in-place, não descarta | `LayoutNormalizer.cs:60-72` |
| `flattenedLayout` do controller | 🔴 **descarta** | `Controllers/ParseController.cs:112-120` |
| Qualquer caminho de parsing/split | 🔴 nenhum consulta | `LineSplitter.cs:22-23` |

O parser compensa a ausência com heurísticas de string espalhadas:
`text.StartsWith("EDI_DC40")`, `Name.StartsWith("LINHA")`, `InitialValue.StartsWith("ZRSDM_")`.

---

## 2. Decisão

**Promover `WithBreakLines` a discriminador canônico de formato físico posicional**, materializado
como um conceito explícito de domínio derivado **do layout**, não do conteúdo do documento:

```
PositionalFormat = WithBreakLines ? RecordPerLine (IDOC)      // registro por linha, sem Sequencia
                                  : ContinuousStream (MQ)      // stream contínuo, Sequencia de 6 chars
```

Consequências diretas da decisão:

1. `LineSplitter` e a regra `offset += 6` passam a consultar `PositionalFormat`, **não** `LayoutType`
   nem heurísticas de string.
2. As heurísticas de conteúdo existentes viram **fallback deprecado com log de Warning** — permanecem
   só para layouts legados sem o campo (ver §5), nunca como regra primária.
3. `LayoutType` deixa de ser fonte de verdade para decisão de formato. Continua válido para o resto.
4. **Nenhum caminho novo pode assumir largura fixa de linha** sem antes resolver `PositionalFormat`.

---

## 3. Opções consideradas

### Opção A — Discriminador no layout via `WithBreakLines` ✅ **ESCOLHIDA**

- **Prós:** determinístico e testável (não depende do conteúdo do documento); alinha o runtime com o
  mapeador, que já modela certo; o dado já existe em todos os layouts novos; uma única decisão
  explícita substitui três heurísticas espalhadas; documentável e auditável.
- **Contras:** exige migração para layouts legados sem o campo (§5); exige corrigir a cadeia de perda
  do campo (§4, três pontos); a mudança toca o caminho crítico de parsing — regressão em MQSeries
  seria grave.

### Opção B — Mais heurística de conteúdo ❌ **REJEITADA**

Detectar IDOC vs MQ por inspeção do texto (ex.: presença de `\n`, `EDI_DC40` no início, contagem de
linhas de largura uniforme).

- **Prós:** zero migração, não depende da qualidade do XML do layout, entrega mais rápida.
- **Contras (decisivos):** empilha exatamente o mecanismo que **causou** a corrupção — heurística
  frágil que falha em silêncio; um IDOC sem `EDI_DC40` na primeira linha, ou um MQ com erro de
  formatação que introduz `\n`, classifica errado e volta a corromper sem alarme; o dado autoritativo
  (`WithBreakLines`) continuaria ignorado; e a decisão passa a depender do documento (variável) em vez
  do contrato (estável). **Rejeitada.**

### Opção C — Novo `LayoutType` (ex.: `"IdocPositional"`) ❌ **REJEITADA**

- **Contras:** exige reescrever layouts já cadastrados no banco e quebra compatibilidade com o
  mapeador low-code e com o Sysmiddle, que emitem `TextPositional`. O `WithBreakLines` já resolve sem
  tocar em `LayoutType`. Custo alto, ganho nulo sobre a Opção A.

---

## 4. Consequências

### 4.1 Bloqueador estruturante — o campo não chega onde a decisão é tomada

**O `WithBreakLines` é sempre `false` no pipeline de parse hoje**, independente do XML do layout.
Não porque o layout diga `false`, mas porque `ParseLayoutFromXDocument`
(`Services/Implementations/LayoutParserService .cs:1002-1010`) simplesmente **não lê o elemento**.

Consequência prática: **ligar `LineSplitter` em `layout.WithBreakLines` sem corrigir o loader não muda
nada** — todo layout continuaria classificado como `ContinuousStream` e o IDOC continuaria corrompido,
agora com a falsa sensação de estar corrigido. A cadeia de perda tem três pontos, e todos precisam ser
fechados na Fase 2:

1. `LayoutParserService .cs:1002-1010` — **não lê** o campo do XML → adicionar à projeção.
2. `LayoutNormalizer.cs:15-23` — `ReestruturarLayout` cria `new Layout` sem copiar o campo.
3. `ParseController.cs:112-120` — `flattenedLayout` é montado sem o campo (Fase 3, do Dex).

### 4.2 Tri-estado: `bool` não distingue "ausente" de "false"

`GetNodeBoolValue` (`XmlLayoutLoader.cs:169-173`) devolve `false` para **ausente**, **`false`** e
**valor malformado** — os três colapsam no mesmo valor. Isso é aceitável para o fluxo Generation, mas
**não** para uma decisão de formato: "ausente" precisa cair no fallback heurístico + Warning, enquanto
"`false` explícito" deve ser respeitado como `ContinuousStream` sem log.

**Decisão:** a resolução de `PositionalFormat` deve enxergar o tri-estado (campo presente e `true` /
presente e `false` / ausente). O mecanismo fica a critério da Lia (`bool?` na entidade, flag
`HasWithBreakLines`, ou resolver que recebe o `XElement`), desde que o **fallback por ausência seja
logado como Warning** com o nome do layout — esse log é o instrumento de medição da migração (§5).

### 4.3 Efeitos colaterais positivos

- Destrava a Fase 3 (generalizar o gate de transformação para IDOC) **com segurança**. Sem esta ADR,
  abrir o gate trocaria "XML vazio" por "XML preenchido com dado fiscal errado" — e alimentaria o
  dataset com pares envenenados. **Fase 2 é pré-requisito duro da Fase 3.**
- Dá ao dataset low-code um rótulo de formato confiável (Fase 4), separando os dois formatos que hoje
  se misturam sob `TextPositional`.

### 4.4 Riscos

| Risco | Severidade | Mitigação |
|---|---|---|
| Regressão em MQSeries (caminho de produção mais usado) | **Alta** | Teste de controle byte-a-byte obrigatório (§6 do spec / checklist do Quinn) |
| Layout legado com `WithBreakLines` ausente classificado errado | Média | Fallback para heurística atual + Warning; comportamento idêntico ao de hoje |
| Layout com `WithBreakLines` **errado** no XML (diz `false` num IDOC) | Média | Não detectável automaticamente — mitigar com a validação cruzada de §5.3 |
| Amostras já persistidas no dataset com output corrompido | **Alta** | Quarentena (Fase 2, política da Lia) — reprocessar sem isso treina RAG em lixo |

---

## 5. Plano de migração dos layouts legados

### 5.1 Regra de resolução (ordem de precedência)

```
1. <WithBreakLines> presente no XML          → usa o valor. Decisão final, sem log.
2. Ausente + heurística de conteúdo conclusiva → usa a heurística. LOG WARNING (layout name + formato inferido).
3. Ausente + heurística inconclusiva          → ContinuousStream (comportamento de hoje). LOG WARNING.
```

O caso 3 preserva o comportamento atual bit a bit para qualquer layout legado — **a mudança é
estritamente aditiva para quem já funciona**.

### 5.2 Instrumentação como métrica de migração

O Warning do caso 2/3 é a medição: enquanto houver Warnings, há layouts não migrados. A migração
está completa quando o log fica limpo em uma janela representativa de uso.

> **Pré-condição não atendida:** não temos hoje o inventário de quantos layouts cadastrados no banco
> têm ou não o elemento `<WithBreakLines>`. Levantar isso é um trabalho de consulta ao catálogo
> (`MapperDatabaseService` / cache de layouts) — **não estimar por amostragem**. Sem esse número não
> dá para dimensionar o esforço da migração nem definir prazo.

### 5.3 Backfill dos layouts sem o campo

Ordem recomendada, do mais barato ao mais caro:

1. **Layouts com heurística conclusiva** — escrever `<WithBreakLines>` explícito no XML do layout com
   o valor que a heurística infere, validando contra um documento real de cada layout antes de gravar.
2. **Layouts sem documento de exemplo** — deixar no fallback. Não adivinhar: um valor errado gravado é
   pior que um Warning recorrente, porque some do radar.
3. **Validação cruzada (defesa contra `WithBreakLines` errado):** se o formato resolvido for
   `ContinuousStream` mas o conteúdo tiver quebras de linha em posições que não são múltiplas do
   tamanho de linha resolvido — ou vice-versa — **logar Warning de divergência**. Não corrigir
   automaticamente: o objetivo é tornar visível a contradição entre contrato e dado, não escondê-la.

### 5.4 O que esta ADR **não** decide

- A política concreta de quarentena/reprocessamento das amostras já persistidas — é entrega da Lia na
  Fase 2, referenciada pela Fase 4.
- O destino final das heurísticas de string. Ficam como fallback deprecado; a remoção definitiva
  depende do inventário de §5.2 e vira ADR própria quando o log estiver limpo.
