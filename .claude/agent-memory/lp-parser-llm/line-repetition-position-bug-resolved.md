---
name: line-repetition-position-bug-resolved
description: "Issue #37 fechada como bug real (nao vestigial) - IsPositionalGroupRepetition sinaliza concatenacao de ocorrencias (infCpl da NFe) mas o comportamento nunca foi implementado; escalado para @lp-architect"
metadata:
  type: project
---

**Conclusão (2026-08-12, issue #37):** `IsPositionalGroupRepetition` (`Models/Entities/LineElement.cs:24`) **NÃO é dado morto** — é um bug real ativo, com layout de produção que depende dela. Escalado para `@lp-architect` (Aria) decidir o design da correção; nada foi implementado nesta sessão.

**Evidência decisiva que faltava na investigação anterior (ver [[project-ecosystem]] e memória antiga em `~/.claude/.../line-repetition-position-bug.md`):** no `LAY_TXT_MQSERIES_ENVNFE_4.00_NFe.xml`, ~15 `LineElement`s têm `MaximumOccurrence > 1` (999/9999) mas **só um** (`LINHA081`) tem `IsPositionalGroupRepetition=true` — todos os outros `false`. Não é ruído de config: é um sinal deliberado, distinto de "pode repetir" (que já é coberto por `MaximumOccurrence`, funcional e usado no loop real de parse em `LayoutParserService .cs:342-355`/`ParseLineFields`).

**O que a flag deveria acionar:** `LINHA081` forma o campo `infCpl` da NF-e por **concatenação de múltiplas ocorrências físicas** num único valor lógico (confirmado contra documento real — 4 ocorrências no MQSeries formam 1 `infCpl`; ver `docs/architecture/poc-excel-generator.md` §7.3/7.4 item F11). Grep em `Services/` por `infCpl`/`Concatena`/`concatenat` não retorna **nada** — essa lógica de agregação nunca foi escrita. O runtime atual trata cada ocorrência como campo independente (`ParsedField.Occurrence = 1,2,3,4`), não como fragmentos de um campo lógico único.

**Confirmação do path de runtime real (fecha o gap da memória anterior):** `ParseLineFields` (chamado em `LayoutParserService .cs:347`, distinto de `CalculateLineValidationRecursive` usado só no monitoramento) calcula `Start`/posição **relativa à própria linha física** via `CalculateLineOffset` — reinicia a cada ocorrência, o que está correto para leitura posicional simples. O gap não é de posição, é de **agregação**: nada une o valor das N ocorrências físicas em 1 campo lógico `infCpl`.

**Decisão pendente para `@lp-architect`:** como agregar ocorrências (concatenar por campo? delimitador? isso muda só leitura interna ou também o `ParsedField` exposto pela API/`/api/parse/upload`?) — não decidir/implementar às cegas, é dado fiscal de produção.

**Ação tomada:** comentário postado na issue #37 (`gh issue comment 37 --repo LayoutParser/LayoutParserApi`) com a evidência completa. Nenhum código alterado; branch `fix/line-repetition-investigation` criada a partir de `feat/identidade-do-bff` sem commits.
