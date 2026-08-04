# Política de quarentena das amostras persistidas (ADR-001)

> **Autora:** @lp-parser-llm (Lia) · **Data:** 2026-08-03 · **Status:** Vigente
> **Contexto:** entrega 2 da Fase 2 de [`adr-001-discriminador-formato-posicional.md`](adr-001-discriminador-formato-posicional.md)
> **Consome:** Fase 4 (dataset low-code) · **Pré-requisito da:** Fase 3 (abertura do gate para IDOC)

---

## 1. Pergunta que esta política responde

O parsing de IDOC produzia **corrupção silenciosa** (100% dos campos deslocados em 6 chars).
As amostras já persistidas em disco foram geradas nesse regime. Reprocessá-las ou usá-las como
few-shot do RAG significaria treinar em cima de dado envenenado?

**Resposta curta: não do jeito que se temia — mas por um motivo que só aparece depois de rastrear
a origem de cada artefato, e há três problemas reais e diferentes do esperado.**

---

## 2. O que realmente foi contaminado (rastreamento de origem)

O bug vivia em `LayoutParserService` e corrompia `ParsedFields`. Rastreando quem consome
`ParsedFields`, só existem quatro consumidores — `LayoutParserService` (interno),
`ParseController` (response ao front), `DataGenerationController` e `TestController`.
**Nenhum serviço de aprendizado, dataset ou RAG lê `ParsedFields`.**

| Artefato persistido | Deriva do parse corrompido? | Origem real |
|---|---|---|
| `Examples/<layout>/*.txt` | ❌ Não | cópia byte-a-byte do upload (`txtFile.CopyToAsync`) |
| `Examples/<layout>/layout_learned.json` | ❌ Não | `LayoutLearningService` lê o TXT do disco, não o parse |
| `LowCodeTransformations/*.input.txt` | ❌ Não | `txtContent` cru |
| `LowCodeTransformations/*.lowcode.xml` | ❌ Não | saída do **runner x86 externo** (motor Sysmiddle), que não usa `LayoutParserService` |
| `MLData/DocumentPatterns/*.json` | ❌ Não | features extraídas do texto cru |

**Conclusão:** o defeito corrompeu **o que o usuário via**, não **o que foi gravado**. Não existe
saída de parse corrompida persistida — logo, **não há quarentena por valor de campo errado a fazer.**

> Isso não é sorte: é consequência de o pipeline de aprendizado ser alimentado pelo documento de
> entrada, não pelo resultado do parse. Vale registrar como propriedade a preservar.

---

## 3. Os três problemas que EXISTEM

### 3.1 Rótulo de formato ausente e não-confiável 🔴 (o mais grave)

O `meta.json` grava `detectedType`, que vem do **detector por conteúdo/extensão** — exatamente o
mecanismo que a ADR-001 rebaixou a fallback. Não existe nenhum campo que registre o
`PositionalFormat` resolvido nem a procedência dessa resolução.

Efeito prático: é **impossível**, olhando o store, separar amostra MQSeries de amostra IDOC de forma
confiável, ou saber se a amostra foi produzida antes ou depois desta correção.

### 3.2 Ausência sistemática de IDOC no store low-code

O gate `detectedType == "mqseries"` (`ParseController.cs:174`) impediu que **qualquer** documento
IDOC chegasse ao store. Verificado no snapshot do servidor
(`.claude/tmp/servidor/layoutparser/api/MLData/LowCodeTransformations`): **0 `meta.json`, 0 `input.txt`**.

Ou seja: **não há nada de IDOC para quarentenar hoje** — o dataset é MQ-puro por construção.
O risco não é retroativo, é **prospectivo**: quando a Fase 3 abrir o gate, o store passa a receber
IDOC pela primeira vez. A marcação de proveniência precisa existir **antes** disso, senão o dataset
nasce misturado e sem rastro.

### 3.3 Features de ML com viés de largura fixa

`DocumentFeatureExtractor.cs:33` calcula `lineCount = documentContent.Length / expectedLineLength`.
Para IDOC (largura variável por segmento) isso é aritmética sem significado. Mesma classe de erro do
defeito original — assumir largura fixa — em outro lugar do código.
`Examples/.../layout_learned.json` tem o análogo: `LineLength` é o comprimento da **primeira linha**
(no Marelli, `518`), tratado como se fosse o tamanho de todas.

---

## 4. Política

### 4.1 Marcação de proveniência (obrigatória a partir da Fase 3)

Todo artefato novo do store low-code grava no `meta.json`:

```json
{
  "positionalFormat": "RecordPerLine",
  "positionalFormatSource": "LayoutMetadata",
  "provenanceContract": "adr-001"
}
```

- `positionalFormat` / `positionalFormatSource` vêm de `PositionalFormatResolver` — **não** do detector.
- `provenanceContract` marca a versão do contrato de parsing; sem ele, amostras de eras diferentes
  ficam indistinguíveis (foi exatamente o que aconteceu com as anteriores).

### 4.2 Segregação do acervo anterior — por sentinela, não por reescrita

As amostras antigas não têm campo de versão. O único discriminador disponível é a **data**.
Gravar um sentinela na **raiz** do store (não tocar nos arquivos existentes):

```json
// MLData/LowCodeTransformations/_provenance.json
{
  "adr": "adr-001",
  "cutoffUtc": "<data/hora do deploy da Fase 2>",
  "before": {
    "positionalFormatSource": "unknown",
    "assumedFormat": "ContinuousStream",
    "rationale": "gate detectedType=='mqseries' impedia IDOC; acervo é MQ-puro",
    "eligibleForFewShot": true
  }
}
```

**Por que sentinela e não reescrever os `meta.json`:** é idempotente, auditável, preserva o
artefato original intacto e custa O(1) em vez de O(N). Reescrever em massa metadados de amostras
para registrar um fato que vale para o lote inteiro é churn com risco de corromper o que hoje está
íntegro.

### 4.3 Veredito por artefato

| Artefato | Ação |
|---|---|
| `Examples/*.txt` | **Não quarentenar.** É a entrada fiel; continua válido como corpus. |
| `Examples/layout_learned.json` | **Não quarentenar; revalidar.** Reprocessável a custo zero (deriva só do TXT). Regerar depois de corrigir o viés de largura fixa. |
| `LowCodeTransformations/*` (input + lowcode.xml) | **Não quarentenar.** Independentes do parse; acervo é MQ-puro. |
| `DocumentPatterns/*.json` de layout `RecordPerLine` | **Quarentenar** — features de largura fixa não descrevem IDOC. Hoje o conjunto é vazio; a regra vale para o que vier. |
| Qualquer amostra IDOC **futura** sem `positionalFormatSource` | **Bloquear** a entrada no few-shot. |

### 4.4 Regra de entrada no RAG/few-shot

Amostra IDOC só é elegível para few-shot com `positionalFormatSource == "LayoutMetadata"`.
Resolvida por `LegacyContentHeuristic` ou `LegacyDefault` → marcar `provisional` e **não** usar como
exemplo, até o layout de origem receber `<WithBreakLines>` explícito.

Motivo: few-shot propaga o erro com autoridade. Um exemplo cuja própria classificação de formato foi
adivinhada é o pior candidato possível a ensinar o modelo a gerar transformação.

### 4.5 O que NÃO fazer

- **Não reprocessar o acervo em massa com o parser corrigido.** Não há saída de parse persistida
  para regenerar; e o `lowcode.xml` vem do runner externo, então o reprocessamento gastaria runner
  sem alterar um byte.
- **Não apagar amostras antigas.** Elas são MQSeries válidas — e o MQSeries está provado
  byte-a-byte idêntico após esta correção.
- **Não usar `detectedType` como rótulo de formato** em nenhuma feature nova de dataset.

---

## 5. Pendências que esta política deixa explícitas

| Pendência | Dono sugerido | Bloqueia |
|---|---|---|
| Gravar os 3 campos de proveniência no `meta.json` | @lp-backend-dev | Fase 4 |
| Criar o `_provenance.json` no deploy da Fase 2 | @lp-devops | — |
| Corrigir o viés de largura fixa em `DocumentFeatureExtractor.cs:33` | @lp-backend-dev | qualidade das features IDOC |
| Inventário de layouts sem `<WithBreakLines>` no banco | — (ADR-001 §5.2, segue em aberto) | fim do fallback deprecado |
