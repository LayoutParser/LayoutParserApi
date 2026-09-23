---
name: project-board-sync-2026-09-16
description: Board-sync completo autorizado pelo dono — 30 issues faltantes adicionadas ao Project #2, board agora 1:1 com o repo (104/104).
metadata:
  type: project
---

Sincronização completa em 2026-09-16 (mesma sessão do sync parcial de #425/#429/#430 e
#366/#414/#417, ver histórico abaixo). O dono autorizou explicitamente uma varredura completa
depois do achado da sessão anterior (board defasado desde ~#313).

**Resultado: board passou de 74 para 104 itens — 1:1 com `gh issue list --state all` (104 issues,
9 abertas / 95 fechadas). Todos os 104 itens do board são `type: Issue` (nenhuma PR rastreada) —
confirmado antes de comparar contagens.**

30 issues adicionadas nesta rodada:
- **24 fechadas → Status Done:** #313, #322, #337, #338, #340, #341, #345, #346, #351, #352,
  #355, #356, #367, #368, #373, #376, #377, #378, #379, #380, #381, #391, #415, #416.
- **6 abertas → Status Todo** (sem evidência de "In Progress", não inventado): #408 (bug login
  401 Microsoft), #413 (docs gate #200), #421, #422, #423, #424 (contratos novos do Fiscal Test
  Lab, dependência da API para issues do LayoutParserReact #201/#204).

Itens que já estavam no board **não foram tocados** — só adição, sem reavaliar status/prioridade
do que já existia (conforme instrução do dono).

**Instabilidade de API durante a rodada:** #423 e #424 falharam no primeiro `gh project
item-add` (um com erro GraphQL "Something went wrong...", outro com saída vazia/parsing
silencioso) — mesmo padrão intermitente já registrado em
[[reference-gh-cli-setup]] (#122, #151). Resolvido com retry simples da chamada individual
(sem precisar de loop `until`/`sleep` desta vez, só reexecutar depois de alguns segundos).
**How to apply:** ao rodar lotes grandes de `item-add`, sempre validar no fim se o `id` retornado
não veio vazio antes de seguir para o `item-edit` de status — capturar isso te salva de um item
"meio adicionado" sem status setado.

Why: ninguém tinha rodado uma varredura completa desde antes de #373 (board ficava desatualizado
silenciosamente, só sincronizado quando alguém pedia issues específicas).

How to apply: repetir esta varredura completa (`gh issue list --state all` vs `item-list
--limit 300`, comparando por `content.number`) periodicamente, não só quando o dono menciona
issues pontuais — o board não se autoatualiza. Confirmar sempre se o board só rastreia `type:
Issue` antes de comparar contagens (aqui era o caso; se algum dia tiver PR misturado, a conta
"104 vs 104" não bate por definição).

Related: [[reference-gh-cli-setup]]
