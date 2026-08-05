---
name: validar-suite-nova-por-mutacao
description: Suíte verde não prova invariante coberta. Ao escrever/estender testes, reintroduza a mutação de cada invariante que o teste alega proteger e confirme que ele falha — antes de declarar pronto.
metadata:
  type: feedback
---

Ao criar ou estender uma suíte de testes, **valide cada teste nos dois sentidos**: reintroduza a
mutação do defeito que ele alega proteger, confirme que ele **falha**, reverta e confirme que
volta ao verde. Só então declare pronto. Faça isso por invariante, não só por bug conhecido.

**Why:** em 2026-07-31 entreguei 29 testes verdes e o `@lp-qa` (Quinn) rodou mutation testing de
verdade — 15 mutações, 13 pegas, **2 escaparam**, ambas por passagem vacante:
(1) apagar o `.Where(g => g.Timestamp <= cypress.Timestamp)` de `ApplyCypressMerge` mantinha 29/29,
porque em todos os cenários que escrevi a geração era anterior ao POST e o limite superior nunca
era exercido — uma invariante load-bearing sem teste, apesar da suíte "cobrir o merge";
(2) o round-trip assertava `XsdValido`/`CypressValidado` nulos mas não `CStatPollux`, que é o único
campo que passa pelo método onde a mutação batia. Cobertura de linha estava lá; cobertura de
comportamento, não.

**Também vale para teste que PENDURA em vez de falhar.** Em 2026-08-05, mutar "token não chega ao
processo do runner" fez `Assert.ThrowsAnyAsync<OperationCanceledException>` esperar para sempre (sob
o defeito, a task nunca completa) — a suíte inteira travou sem diagnóstico e foi preciso
`taskkill /F /IM testhost.exe`. xUnit não tem timeout por teste por default. Em teste de
concorrência/cancelamento, **toda espera precisa de teto explícito**
(`Task.WhenAny(tarefa, Task.Delay(n))` + `Assert.Fail`). Consequência prática: rode a bateria de
mutação em background e cheque o progresso por `git status`, senão uma mutação que trava consome a
janela inteira.

**How to apply:** o padrão barato é `git archive HEAD` pro scratchpad, mutar lá e rodar a suíte —
nunca mutar a árvore de trabalho e torcer pra lembrar de reverter (se precisar mutar in-place,
confirme depois com `git diff --stat` no código de produção antes de commitar). Priorize mutar:
limites de janela/intervalo (`<=`, `>=`), retornos nulos, e o ramo "fail-closed" de qualquer
guarda de segurança. Ver também [[isolated-log-testing-harness-pattern]] e a matriz do Quinn em
`.claude/agent-memory/lp-qa/tecnica-matriz-de-mutacao.md`.
