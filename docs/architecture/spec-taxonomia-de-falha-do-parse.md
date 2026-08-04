# Spec — Taxonomia de falha do parse e identidade de campo no erro

> `@lp-architect` (Aria), 2026-08-03. Origem: pergunta do dono do projeto — *"falhou por quê?
> Se a falha é nossa, não apresenta o arquivo; se a falha é do arquivo, precisamos apontar o erro,
> por isso a ML está aprendendo sobre arquivos corretos e incorretos, para apontarmos onde a tag
> errada está influenciando a transformação do TXT para o XML (NF-e emissão)."*
>
> Esta spec é o **contrato** contra o qual back-end e front-end codam **em paralelo**. Quem
> implementar não deve divergir dela sem me avisar.

## 1. O problema

Hoje o `ParseController` tem dois desfechos e três realidades:

| Realidade | Hoje | Problema |
|---|---|---|
| Nosso parser quebrou (bug nosso) | `422` + `message` string | Indistinguível da linha abaixo |
| Arquivo tão quebrado que lança exceção | `422` + `message` string | Indistinguível da linha acima |
| Arquivo parseável, com defeito localizável | `200` + `validationErrors` | Estruturado, mas só sobre **tamanho de linha** |

Duas consequências: (a) o `422` culpa o arquivo do usuário mesmo quando a culpa é nossa; (b) o erro
não tem **identidade de campo**, então não serve de rótulo para a IA aprender atribuição por tag.

`Models/Parsing/DocumentValidationErrorInfo.cs` tem hoje `LineIndex`, `Sequence`, `ExpectedLength`,
`ActualLength`, `StartPosition`, `EndPosition` — um intervalo de bytes, nenhuma identidade de campo.
Um dataset rotulado assim ensina *"linha 37, colunas 100-140 está errada"*, o que **não generaliza**:
noutro documento a mesma tag está em outra posição. O modelo aprenderia endereço, não semântica.

## 2. Contrato alvo

### 2.1 `200` — parse conseguiu, com ou sem defeito

Documento **sempre** renderizável. Defeito localizável **não** é `422`: é entidade processável com
problema, e o usuário precisa ver o documento com o erro anotado.

```jsonc
{
  "success": true,
  "detectedType": "mqseries",
  "documentHealth": "clean" | "has_defects",   // NOVO — derivável de validationErrors, explícito para a UI
  "layout": { }, "fields": [], "text": "", "summary": { },
  "documentStructure": { }, "lineValidations": [],
  "validationErrors": [
    {
      "lineIndex": 37, "sequence": "A", "errorMessage": "...",
      "expectedLength": 600, "actualLength": 578,
      "startPosition": 100, "endPosition": 140,
      // ── NOVOS (item 3) ──
      "fieldName": "vNF",                    // nome do elemento no layout — null se não resolvível
      "fieldGuid": "FLD_...",                // identidade estável do campo — null se não resolvível
      "targetXPath": "/NFe/infNFe/total/ICMSTot/vNF"  // destino no XML de saída — null enquanto a linhagem não existir
    }
  ],
  "transformations": null, "transformationsStatus": "not_applicable"
}
```

### 2.2 `422` — irrecuperável, não há documento a mostrar

```jsonc
{
  "success": false,
  "failureCause": "document_malformed" | "layout_invalid",
  "detectedType": "unknown",
  "message": "...",
  "correlationId": "..."
}
```

> **DECISÃO (Aria, 2026-08-04, após implementação).** O `@lp-backend-dev` propôs dividir os dois
> rótulos **por artefato** — qual arquivo o usuário deve olhar — em vez de por natureza abstrata do
> erro. **Aceito: é melhor que a versão original desta spec**, porque é acionável. Com dois ajustes:
>
> 1. **`layout_mismatch` renomeado para `layout_invalid`.** O rótulo passou a significar "o XML do
>    layout está ilegível" — uma propriedade de **um** artefato. "Mismatch" nomeia uma **relação**
>    entre dois, e usar a palavra para outra coisa envenena o vocabulário.
> 2. **`layout_mismatch` fica RESERVADO** para o caso que o Dex caracterizou em teste e (corretamente)
>    não implementou: XML bem-formado que **não é um layout** — o usuário subiu o arquivo errado.
>    Hoje isso não lança exceção, vira layout sem elementos e "sucede" com zero campos. É o caso que
>    o usuário final mais quer ver sinalizado, e é decisão de produto transformá-lo em falha — fica
>    fora desta leva, com o nome guardado.
>
> **Documento vazio → 422 `document_malformed`: aceito.** O argumento do Dex é o certo — sem isso o
> payload sairia `documentHealth: "clean"` para um documento sem conteúdo, e o item 2 nasceria
> mentindo. Um 200 "limpo" para arquivo vazio é exatamente a falha silenciosa que esta spec existe
> para matar.

### 2.3 `500` — defeito nosso

```jsonc
{
  "success": false,
  "failureCause": "parser_defect",
  "message": "Falha interna ao processar o documento.",  // mensagem segura, SEM stack trace
  "correlationId": "..."
}
```

## 3. Como classificar — regra não-negociável

O `ParseAsync` captura exceção internamente e devolve `Success=false` + `ErrorMessage`. A
classificação sai do **tipo da exceção**:

- Exceções conhecidas de entrada ruim (XML do layout malformado, encoding inválido, layout que não
  casa com o documento) → `document_malformed` / `layout_mismatch` → **422**.
- **Qualquer outra** (`NullReferenceException`, `IndexOutOfRangeException`, etc.) → `parser_defect`
  → **500**.

> **O default é culpar a nós, não ao usuário.** Exceção não catalogada é defeito nosso até prova em
> contrário. Dizer "seu arquivo está errado" quando não sabemos é pior que um 500 honesto: manda o
> usuário caçar problema em arquivo bom, e some com o sinal de que temos um bug.

## 4. Divisão de trabalho

| Item | Back-end (`@lp-backend-dev`) | Front-end (`@lp-front-dev`) |
|---|---|---|
| 1 · `failureCause` | Classificar por tipo de exceção; emitir o campo nos 422/500 | Decidir esconder × mostrar anotado a partir do campo |
| 2 · Semântica de status | Mover defeito localizável para `200`; `422` só irrecuperável; bug nosso → `500` | Tratar o caminho "200 com defeitos" (hoje só trata 200-limpo e erro) |
| 3 · Identidade de campo | Acrescentar `fieldName`/`fieldGuid`/`targetXPath` ao erro | Anotar o campo na árvore/estrutura, não só a linha |

**Dependência:** o front depende do **contrato**, não da implementação. Com esta spec fechada, os dois
lados começam em paralelo — o front codifica contra os campos novos tratando-os como opcionais
(`null`) até o back-end emitir.

## 5. Faseamento honesto

Os itens 1 e 2 são pequenos e independentes. **O item 3 não é.** `targetXPath` depende da linhagem
campo→XPath, que é lacuna conhecida do projeto (o catálogo GUID→XPath resolve saída, não entrada).

Portanto: `fieldName`/`fieldGuid` primeiro (se o validador souber a que elemento o erro pertence);
`targetXPath` fica `null` até a linhagem existir, **sem bloquear o resto**. Se o validador de hoje
só souber a linha e não o campo, isso é investigação antes de código — reporte em vez de inventar
uma identidade que o dado não sustenta.

### 5.1 DECISÃO após a investigação (Aria, 2026-08-04)

O `@lp-backend-dev` investigou e o veredito é: **o dado não sustenta identidade de campo.**
`DocumentValidationService.ValidateDocument(documentContent, expectedLineLength)` recebe **só texto e
um número** — nunca vê o `Layout`. Os cinco erros que emite são de **enquadramento de linha** (linha
incompleta, excede N chars, sequência inválida, HEADER fora do lugar). Nenhum é escopado a campo.
Não há palpite honesto a fazer, e ele fez certo em parar antes de implementar.

**O que ele achou de aproveitável:** o `Sequence` de 6 chars do erro é a mesma chave que
`IsLineValidForConfig` usa para casar linha ↔ `LineElement`. Dá para resolver a identidade do
**registro** (`LineElement.Name` + `ElementGuid`, GUID estável vindo do XML do layout) **sem tocar no
validador** — só no mapeamento dentro do `ParseAsync`, onde `Layout` e `LineErrors` estão ambos em
escopo.

**Decisão: implementar, mas com nome honesto.**

- Acrescentar `recordName` e `recordGuid` ao erro — identidade de **registro/segmento**, que é o que
  o dado realmente sustenta.
- **Manter `fieldName`/`fieldGuid` nulos** até existir validação escopada a campo.

**Por que não preencher `fieldGuid` com o GUID do registro**, que era a tentação óbvia: seria dado
**mal rotulado**. O campo diria "campo" e o conteúdo seria "registro". Um dataset assim ensina à IA
que a granularidade da atribuição é o segmento, e quem consumir depois não teria como saber que o
rótulo mente. Corromper o significado é pior que ter o campo nulo — nulo é honesto, mal rotulado é
armadilha.

Ainda assim o ganho é real e vale agora: `recordGuid` **generaliza entre documentos** (o segmento é
estável), enquanto `startPosition`/`endPosition` não generalizam nada. É sinal grosso, mas é sinal —
e é estritamente mais do que temos hoje.

## 6. Por que isso destrava a IA

Com `fieldGuid` no erro, cada documento processado vira par rotulado *(campo, correto/incorreto)* em
vez de *(intervalo de bytes, errado)*. É o que permite responder "qual tag está quebrando a NF-e" de
forma que generalize entre documentos — e é o que a visão de ML do projeto pressupõe hoje sem ter.
