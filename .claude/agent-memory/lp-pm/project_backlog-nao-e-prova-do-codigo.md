---
name: project-backlog-nao-e-prova-do-codigo
description: Neste repo, o registro de backlog (issue fechada, título de achado, nome de branch) já divergiu do código 3x — sempre verificar a premissa no código antes de formalizar ou confiar
metadata:
  type: project
---

**Neste repositório, o texto do backlog não é evidência do estado do código.** Quatro casos distintos já apareceram, entre 2026-08-14 e 2026-08-18:

1. **#33** (`DataGenerationController` / DI incompleto) — fechada com fix real em `6082834`, mas o merge `612a5a3` ("resolvendo conflito") apagou o bloco de registros do grupo `Generation` do `Program.cs` como dano colateral. Passou despercebida porque o teste de regressão montava um `ServiceCollection` próprio com os registros **copiados à mão** — testava a cópia, não o `Program.cs`. Suíte 100% verde com o bug de volta. Reaberta por mim.
2. **#51** (`AiCandidateStore` sem TTL) — fechada citando `294ca22`, que mexe em `ai/XslSynth/Metrics/RunManifest.cs`, **subsistema diferente**. Nomes parecidos ("retenção/limpeza de artefatos de IA"), problema real intocado. Reaberta por mim.
3. **Achado 4 da auditoria de 2026-08-14** — rotulado como "`CatalogHealthCheck` considera catálogo vazio como saudável". A premissa **nunca se reproduziu**: o guard `if (_state.LayoutCount <= 0) return Unhealthy(...)` já existia desde `a01274a` (P1.3). O commit `d608539` corrige na verdade **outro** bug, mais grave: `CachePermanentWarmupBackgroundService.cs:111` convertia falha de leitura do SQL em "catálogo vazio" e chamava `SetResult(0)`, matando o retry da #67 exatamente no blip transitório de SQL que ele existe para cobrir. Corrigi só a comunicação (corpo + comentário da PR #89), não o código. A branch `fix/catalog-health-catalogo-vazio` segue com nome impreciso, de propósito.
4. **#51 de novo, 2026-08-18** — o *primeiro* fechamento (caso 2 acima) foi reaberto e depois refechado corretamente citando a PR #89, mas essa PR só implementava o TTL; a metade "limite de tamanho" do escopo original (`MaxStoredTickets`) ficou pendente sem ninguém perceber até `@lp-qa` (Quinn) confirmar em 2026-08-18. Implementada em `d917129` (branch `fix/ai-candidate-store-particionamento-e-ttl`). Dessa vez não reabri — só comentei linkando o commit, já que o texto da issue sempre refletiu o escopo completo (TTL + limite), só faltava o segundo commit de fechamento ser registrado.

**Why:** três modos de falha de rastreabilidade — regressão silenciosa por merge, fechamento por semelhança de nome, e achado mal rotulado na origem. Todos produzem o mesmo resultado: o registro diz uma coisa, o código diz outra. O caso 3 mostra que isso vale também para itens **abertos**, não só fechados.

**How to apply:** antes de formalizar um achado como issue, ou de tratar uma issue fechada como resolvida para evitar duplicata, **conferir o código atual**: `git grep` no símbolo/arquivo citado, e `git show <commit>:<arquivo>` para checar se o commit citado toca mesmo o arquivo da issue (e se o guard alegadamente ausente já existia). Se divergir — reabrir/corrigir o registro original em vez de abrir item novo, que fragmenta o histórico; quando o código está certo e só o rótulo está errado, corrigir a comunicação e **deixar explícito que o achado estava mal rotulado**, para o revisor seguinte não se perder. Padrão a desconfiar sempre: teste de DI que não carrega a composição real do `Program.cs` (é a causa raiz do caso 1 e virou a issue #90).

Related: [[reference-gh-cli-setup]], [[feedback-autoridade-pr-edit-vs-create]]
