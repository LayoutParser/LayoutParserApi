---
description: Rastreia o fluxo de parsing de um documento, do upload à estrutura retornada.
argument-hint: <tipo ou sintoma, ex.: "MQSeries detectado como txt">
allowed-tools: Read, Grep, Glob, Bash
---

# /parse-trace

Rastreie o fluxo de **parsing** para diagnosticar: **$ARGUMENTS**

## Cadeia a percorrer

1. `Controllers/ParseController.cs` → `Upload(layoutFile, txtFile, layoutName)`
2. Detecção: `Services/Parsing/Implementations/LayoutDetector.cs` (`DetectType`)
   - ⚠️ lembrar dos *overrides* por extensão (`.mq_series`, `.idoc`) e por nome de layout ("MQ")
3. Parse: `Services/Implementations/LayoutParserService.cs` (`ParseAsync`, `ReestruturarLayout`, `ReordenarSequences`, `BuildDocumentStructure`)
4. Validação de linha: `CalculateLineValidations` (só p/ layouts com tamanho configurado — `LayoutLineSizeConfiguration`)
5. Cache: `Services/Cache/` + `Services/Database/Cached*` (layout vindo do Redis/SQL)
6. Background: `LowCodeAutoTransformationService.RunInBackgroundAsync`

## Saída

- Diagrama do caminho percorrido pelo documento, apontando **onde** o sintoma surge.
- Hipótese da causa raiz + correção mínima sugerida (sem aplicar, salvo se o usuário pedir).
- Cheque logs por `CorrelationId` se houver evidência de runtime.
