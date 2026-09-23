# ADR — Sinais de qualidade do pacote de especificação fiscal (issue #424)

Status: implementado (recorte mínimo). Consumidor: LayoutParserReact#201.

## Contrato (aditivo)

`GET /api/workspaces/{w}/mapping-packages/{id}` — em cada artefato `spec` da revisão mais recente:

```json
{
  "kind": "spec",
  "qualityStatus": "complete",
  "qualityError": null,
  "qualitySignals": {
    "missingRequiredColumns": ["Regras!vICMS"],
    "conflicts": [],
    "absentReferences": [],
    "skippedSheets": ["Layout"],
    "emptySheets": ["SemRegras"],
    "checksRun": ["missingRequiredColumns", "skippedSheets", "emptySheets"]
  }
}
```

Falha de leitura: `qualityStatus: "failed"`, `qualityError` genérico, `qualitySignals` ausente.
Artefatos não-`spec` não têm os campos. Nenhum campo existente muda.

## Regra de leitura (honestidade do contrato)

Array vazio só significa "verificado, sem problema" se o nome do check estiver em `checksRun`.
`conflicts` e `absentReferences` existem por contrato mas **nenhum check os popula** neste recorte:
nunca constam em `checksRun`; vazios ali = "não analisado".

## Decisões

- **Síncrono** (`complete`/`failed`): só cabeçalhos + contagem, mesma passada do inventário. Não há `pending`.
  Calculado na leitura; nada persistido. Falha na análise nunca derruba o GET nem a ingestão.
- **Checks**: `missingRequiredColumns` (formato `"Aba!Coluna"`, comparação case-insensitive),
  `skippedSheets` (expõe `SkippedSheets` do extrator), `emptySheets` (aba de regra reconhecida com 0 regras).
- **Colunas obrigatórias**: não existe lista canônica no código (o extrator só exige o rótulo `Regra` + ≥1
  coluna de condição). Lista única e configurável em `FiscalPackage:Quality:RequiredRuleSheetColumns`
  (default vazio ⇒ o check não roda e não consta em `checksRun`). Cabe ao dono do domínio fiscal defini-la.
- **Fora do recorte**: `absentReferences` (o XSD não é lido no fluxo do pacote), `conflicts` entre abas (issue à parte).
- **Segurança**: só nomes de aba/coluna e contagens; nunca conteúdo de célula em log ou resposta.
