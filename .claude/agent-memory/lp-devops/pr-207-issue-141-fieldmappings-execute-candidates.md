---
name: pr-207-issue-141-fieldmappings-execute-candidates
description: PR #207 (contrato fieldMappings definitivo em execute-candidates, issue #141) — 5º caso do falso positivo SCS0018 por deslocamento de linha, CI verde
metadata:
  type: project
---

PR #207 (`feat/fieldmappings-execute-candidates-141` → `develop`) publicado em 2026-08-28 a
partir do worktree `/mnt/c/Users/elson.lopes/source/repos/LayoutParserApi-wt-141` (já removido
após CI verde). Fecha a cadeia #86→#139→#140→#138→#141.

**Doc solto resolvido:** `docs/architecture/design-contrato-fieldmappings-execute-candidates-issue-141.md`
estava untracked no repo principal (branch errada, `feat/resolucao-estrutural-txt-xml-140`) com
a §9 de QA (resultado de performance) já escrita pelo QA. Copiado para dentro do worktree da
#141 e commitado lá (`7bc616b`) — é o lugar certo, já que o doc é sobre essa issue.

**5º caso do padrão de falso positivo SCS0018 por deslocamento de linha** (mesmo de
[[pr-203-issue-138-sectionmappings-fase0]] e [[pr-200-ci-scs0018-bloqueado]]): baseline tinha
`LowCodeAutoTransformationService.cs:364` e `:408` (mesmos `File.WriteAllTextAsync` de sempre),
código novo da #141 empurrou pra `:370` e `:414`. Corrigido no baseline
(`security-code-scan-baseline.json`, commit `4f92b8b`), CI ficou verde.

**Why:** esse padrão já apareceu em ~5 PRs seguidos na mesma área de código
(`LowCodeAutoTransformationService.cs`/controllers) — toda vez que alguém adiciona linhas acima
de um `File.WriteAllTextAsync`/`Path.Combine` já no baseline, o SCS0018 "novo" é sempre esse
mesmo achado deslocado, nunca um achado real novo. Vale checar visualmente (comparar o código na
linha reportada com o baseline próximo) antes de tratar como bloqueio real.

**Build/test:** `dotnet build` 0 erros; `dotnet test` 467 passando, 4 falhas pré-existentes
(dependentes de path Windows — `LowCodeRunnerArgsTests`, `SafePathResolverTests` — esperadas em
ambiente Linux/WSL, não é regressão).

**Pendências que ficam para o dono (fora do alcance de qualquer agente):**
- Merge do PR #205 (issue #140, base do #141) e do próprio #207 — nenhum merge foi feito por
  `@lp-devops` nesta sessão (fora do escopo).
- Validação comportamental real (20 execuções) contra o LowCodeRunner, para #140 e #141 — bloqueio
  ambiental (precisa de ambiente Windows), documentado nos dois PRs.
