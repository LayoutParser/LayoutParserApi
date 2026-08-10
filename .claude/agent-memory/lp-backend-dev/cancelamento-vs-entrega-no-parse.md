---
name: cancelamento-vs-entrega-no-parse
description: Cancelar a transformação no teto síncrono do parse e "emitir ticket mesmo em processing" se contradizem — só coexistem porque o índice fecha em parcial. Não desfaça um sem o outro.
metadata:
  type: project
---

No pathway low-code do `ParseController`, **cancelar o trabalho no teto síncrono** e **entregar um
ticket consultável mesmo quando o status é `processing`** puxam em direções opostas: se o
cancelamento matasse tudo, o ticket responderia 404 para sempre e o rótulo "(processando...)" só
trocaria de forma. Os dois só coexistem por causa de três decisões acopladas:

1. o índice é **fechado mesmo em falha/cancelamento**, marcado `partial: true`;
2. entrada `partial` é **legível** pelo endpoint, mas **nunca** vira hit de cache-first;
3. o status do payload considera o **token**, não só quem venceu a corrida do `Task.WhenAny` — a
   task cancelada termina quase junto com o `Task.Delay` e, sem essa checagem, o parse reportava
   `completed` com todos os candidatos em erro (pior que `processing`: diz "terminou e falhou"
   quando a verdade é "não deu tempo").

**Why:** implementado em 2026-08-05 (branch `feat/entrega-transformacao-no-parse`) a partir da spec
`docs/architecture/spec-entrega-da-transformacao-no-parse.md`, cujos §1.1 e §2.6 não reconciliam
essa tensão — ela apareceu só na implementação, e o item (3) foi encontrado por um teste que
falhava de forma intermitente.

**How to apply:** ao mexer em cancelamento, cache ou status desse pathway, trate os três pontos como
um conjunto. Remover o fechamento parcial do índice ressuscita o rótulo eterno; deixar `partial`
virar cache congela resultado truncado para todo upload idêntico dentro da janela de frescor.
Ver também [[validar-suite-nova-por-mutacao]].

**Correção factual à spec (§1.1):** o vazamento de slot **não era ilimitado** — o
`RunnerTimeoutSeconds` (15s) já matava o processo e liberava o semáforo no `finally`. O ganho real
do cancelamento é reclamar a janela entre o teto síncrono (6s) e esse timeout, não "uma fila que
nunca encolhe".
