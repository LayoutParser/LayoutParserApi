---
name: project-contrato-linha-vazia-progresso-degradacao-2026-08-27
description: Issues #194-#197 do doc de arquitetura contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27; InformacoesParaEDI já resolvido em PR #191, sem issue nova.
metadata:
  type: project
---

Lote formalizado a partir de `docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md`
(`@lp-architect`, 2026-08-27), que cobria 3 pedidos do dono + 1 achado já resolvido:

- **#194** — story: `IsDeclaredEmpty` em `LineInfo`. Dono `lp-parser-llm`.
- **#195** — story: fases discretas em `LowCodeTransformationIndexEntry.Status` (`uploaded`→`layout_selected`→`parsing`→`transforming`→`completed`/`partial`). Dono `lp-backend-dev`.
  Complementa (não duplica) a **#99** (já existia — instrumentar/documentar o `transformationsTicket`
  existente para o "trava em 100%"); #99 é sobre medição/documentação do que já existe, #195 é sobre
  estender o enum de `Status` em si.
- **#196** — bug: colapso posicional LINHA006 no `.mqseries` (todos os campos com
  `startPosition===endPosition`). Dono `lp-parser-llm`, **bloqueado em `correlationId`** que só o
  dono do projeto pode fornecer — severidade marcada "a validar" (sem confirmação de causa raiz
  nem frequência em produção, só hipótese fundamentada em código).
- **#197** — story: contrato aditivo `PositionalAlignmentFailed` por linha (sinal genérico de
  degradação posicional, sem acoplar a `mapperName` — pedido explícito do dono). Relacionado a
  #196 mas não depende do `correlationId` bloqueante daquela.

**Achado importante nesta sessão:** o bug de `InformacoesParaEDI`/LINHA081 (Length=LengthField em
fragmento bruto + falta de `OccurrenceCount`/`IsAggregatedOccurrence`), que o doc de arquitetura
ainda descrevia como "não implementado", **já foi corrigido e mesclado** — commit `a330af2` na
branch `fix/informacoesparaedi-length-e-occurrence-id`, PR **#191 (MERGED)**, validado por `@lp-qa`
(PASS, 393/393 testes) conforme `.claude/agent-memory/lp-qa/informacoesparaedi-occurrencecount-fix-qa-gate.md`.
Não havia issue prévia pra esse bug (nunca chegou a virar item de board), então **nenhuma issue foi
criada** para ele — só registrado aqui como já resolvido. Reforça [[project-backlog-nao-e-prova-do-codigo]]:
o doc de arquitetura (fonte da tarefa) estava desatualizado em relação ao código real; sempre
verificar o estado atual antes de formalizar como pendência.

Todas as 4 issues adicionadas ao Project #2 (Status=Todo). Field-ids confirmados iguais aos já
registrados em [[reference-gh-cli-setup]] (Tipo: story=`b1173f83`, bug=`fb117f1c`; Dono:
lp-parser-llm=`2cab763a`, lp-backend-dev=`c290c76b`).

**Correção de referência:** `gh` neste ambiente (WSL/bash) está em `/usr/bin/gh` no PATH direto —
o caminho absoluto Windows (`C:\Users\...\gh.exe`) documentado em [[reference-gh-cli-setup]] não
existe neste shell. Atualizar a referência para citar ambos os caminhos possíveis conforme o
ambiente (Windows/PowerShell vs. WSL/bash).
