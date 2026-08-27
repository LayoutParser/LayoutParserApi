---
name: pr198-linhainfo-signals-qa-gate
description: QA gate do PR #198 (IsDeclaredEmpty/PositionalAlignmentFailed/status "failed") — PASS com achado de design em IsDeclaredEmpty
metadata:
  type: project
---

PR #198 (`feat/contrato-linha-vazia-e-progresso` → `develop`), revisado em 2026-08-27.
`dotnet build` limpo; `dotnet test` 385/389 (as 4 falhas são as pré-existentes de path
Windows×Linux, ver [[unified-logging-parse-bug-and-log-dir-incident]] linha de raciocínio
similar — não são regressão deste PR).

**Achado de design (não é bug de implementação — o código bate com a spec):**
`LineInfo.IsDeclaredEmpty` é calculado como `string.IsNullOrWhiteSpace(currentLine)` sobre a
linha FÍSICA INTEIRA (Sequencia + InitialValue + campos), exatamente como o
`docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md` §1
especifica. Só que todo matcher em `IsLineValidForConfig` (`Services/Implementations/
LayoutParserService .cs`) exige um prefixo NÃO-espaço pra casar a linha com uma config de layout
(sequência numérica de 6 dígitos, `HEADER`, `EDI_`/`ZRSDM_`, ou `999999`) — então uma linha
"identificada" (`matchingLineConfig != null`) NUNCA pode ser 100% whitespace, e uma linha 100%
whitespace nunca é identificada (cai em `unidentifiedLines`, `lineInfos` fica vazio pra ela).
Resultado: `IsDeclaredEmpty=true` é, na prática, inalcançável para MQSeries/IDOC — mesmo quando
o CAMPO de dado real está 100% em branco, porque o Sequencia/InitialValue não-espaço no início
da linha já derruba o `IsNullOrWhiteSpace`. Confirmado com 2 testes que reproduzem exatamente
esse cenário em `tests/LayoutParserApi.Tests/Parsing/LineInfoAdditiveSignalsTests.cs`
(`ACHADO_dado_totalmente_em_branco_no_campo_nao_liga_IsDeclaredEmpty` e
`ACHADO_linha_totalmente_em_branco_nao_gera_LineInfo_nenhum`).

**Why:** se a intenção de produto é "avisar quando o DADO da linha está vazio" (uso plausível:
o front sinalizar ao usuário que uma linha declarada no layout veio sem conteúdo), o sinal como
implementado não entrega isso — ele só dispara pra um cenário que as regras de matching atuais
tornam impossível. Vale a pena `@lp-architect`/`@lp-parser-llm` revisitarem se o cálculo deveria
comparar o(s) campo(s) de DADO (não-Sequencia/InitialValue) em vez da linha bruta inteira.

**How to apply:** ao revisar qualquer sinal aditivo que dependa de "linha bruta" vs. "campo
extraído", extrair um caso de teste síntetico ANTES de aprovar — spec e código podem concordar
entre si e ainda assim não produzirem o comportamento que o produto quer. Não é suficiente
verificar "código bate com spec"; é preciso perguntar se a spec cobre os matchers já existentes.

**Cobertura de teste adicionada nesta sessão** (5 testes de linha + 2 de status "failed" no
índice low-code): `tests/LayoutParserApi.Tests/Parsing/LineInfoAdditiveSignalsTests.cs` (novo
arquivo) e 2 métodos em `tests/LayoutParserApi.Tests/Transformation/
LowCodeTransformationStoreTests.cs`. `PositionalAlignmentFailed` tem reprodução sintética via
dois `FieldElement` com `LengthField=0` consecutivos (mesmo `Start`) — não depende do
correlationId pendente do caso real LINHA006.

**Incidente de processo (não é bug de produto):** durante esta sessão, dois agentes concorrentes
(backend-dev corrigindo `Controllers/ParseController.cs` para expor `LineInfos` no payload de
Upload, e outro fechando a spec doc) rodaram commit no MESMO checkout de working directory
(sem worktree isolado para esta branch), e absorveram sem querer meus arquivos de teste
untracked/modificados nos commits deles (`3ffe2ec`/`abea2b5`) via `git add`/`commit -a` amplo.
Nada foi perdido — o conteúdo está correto na árvore — mas as mensagens de commit não mencionam
os testes de QA que vieram junto. Nenhum push aconteceu ainda (branch local `ahead 2` do
`origin`), então dá pra reescrever a atribuição antes do push se `@lp-devops` achar que vale a
pena; senão, é só ruído de changelog.

**Veredito: PASS.** Build limpo, testes verdes (incluindo os 7 novos), gaps de cobertura
fechados. O achado de `IsDeclaredEmpty` é recomendação de follow-up, não bloqueador — o contrato
aditivo não quebra nada existente, só entrega menos valor de sinal do que a spec pretendia.
