---
name: tecnica-matriz-de-mutacao
description: Como eu valido uma suíte de testes neste repo — copiar HEAD com git archive pro scratchpad, reintroduzir cada bug e medir quais testes pegam. Não toca a árvore compartilhada.
metadata:
  type: feedback
---

Para julgar se uma suíte de testes **vale o que promete**, não conte testes: reintroduza os bugs
originais um a um e meça quais são pegos. Contagem de testes e relato do dev ("validei mutando o
sanitizer") não substituem a matriz.

**Why:** no gate do Handoff 1 (Gap 3), 29/29 verdes e o relato do Dex estavam corretos — mas a
matriz achou 2 buracos que nenhum dos dois indicaria: um invariante load-bearing sem teste nenhum
(`.Where(g => g.Timestamp <= cypress.Timestamp)` no merge do Cypress) e uma asserção faltando no
único teste que protege contra divergência escritor/leitor. Teste verde não é o mesmo que teste
que pega o bug. Mesma lição do gate anterior em [[ai-metrics-gap3-qa-gate]]: revisão de código não
substitui execução.

**How to apply** (receita que funcionou, ~20s por mutação):

1. Copie o HEAD pra **fora** do repo, sem tocar em `.git` nem no working tree:
   `git archive HEAD | tar -x -C <scratchpad>/mutation` (91 MB / 764 arquivos aqui, segundos).
   Nunca mute arquivo dentro do repo — **há sessões concorrentes editando a mesma árvore**, e
   uma restauração malfeita vira conflito pra outra pessoa. Já aconteceu de outra sessão commitar
   no meio do meu gate.
2. Rode o baseline na cópia e confirme que reproduz (mesmo nº de passes).
3. Driver em Python: para cada mutação, `str.replace(old, new, 1)` → `dotnet test` → parse do
   `Failed: N, Passed: N` + nomes dos testes → **restaura o arquivo original**. Sempre restaure a
   partir da string lida antes, não de um segundo replace inverso.
4. Classifique: `PEGOU` / `NÃO PEGOU`. Um `NÃO PEGOU` é achado de cobertura, e para cada um
   escreva o teste faltante **na cópia** e prove as duas direções: passa com código intacto,
   falha com a mutação. Só então recomende ao dev — a correção sugerida já vem validada.
5. Nem todo `NÃO PEGOU` é defeito: comparação em tempo constante (`FixedTimeEquals` → `==`) é
   funcionalmente indistinguível e se verifica por leitura. Diga isso explicitamente em vez de
   reportar como buraco.

**Armadilha desta máquina:** o heredoc do bash aqui **colapsa `\\` em `\`** — padrões com escape
(regex, `Replace("\r\n", ...)`) chegam errados ao script e a mutação vira "pattern não encontrado"
silencioso. Monte esses literais com `chr(92)` no Python, e sempre trate `[SKIP-PATTERN]` como
falha da ferramenta, não como "não pegou".
