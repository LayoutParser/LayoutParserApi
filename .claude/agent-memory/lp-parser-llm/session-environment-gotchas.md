---
name: session-environment-gotchas
description: Esta sessão/máquina (2026-07-21) não tinha nenhum dado de sessão anterior em .claude/tmp (nem mapeador descriptografado, nem LayoutVO, nem XSD/PDF baixados) — tudo teve que ser buscado de novo em fontes públicas; sem pip/apt com root, sem gh CLI
metadata:
  type: project
---

Verificado 2026-07-21: `.claude/tmp/` nesta sessão só tinha o `.gitignore` —
nenhum dos artefatos que memórias anteriores (minhas e da Aria) citam como "em
disco" estava presente: sem `.claude/tmp/export/MAP_MQSERIES_SEND_ENV_TXT_XML_NFE.decrypted.xml`,
sem `.claude/tmp/servidor/layoutparser/...` (gabaritos, LayoutVO, XSD/PDF da
NT), sem `Documentos/Layout/layout-nfe.xml` no working tree (esse nem existe
como pasta aqui). Ou seja: **`.claude/tmp` e `Documentos/` são LOCAIS por
sessão/máquina, não persistem entre sessões Claude Code diferentes** — mesmo
sendo "a mesma máquina" na teoria (ver `multi-session-execution-plan.md` §0),
pelo menos esta instância de sessão começou vazia. Não presumir que dado de
uma memória antiga ("X está em disco em .claude/tmp/Y") ainda está lá — checar
primeiro.

**Ferramentas ausentes nesta máquina:** sem `pip3`/`python3 -m pip` (nem
`ensurepip`), sem `apt-get` com sudo passwordless (não dá pra instalar nada via
apt sem senha) — qualquer necessidade de biblioteca Python (ex.: `pypdf` que
os docs de arquitetura citam) precisa de alternativa .NET-native (ex.: PdfPig
em vez de pypdf, ver [[nt-pipeline-p1-p2-real-run]]) ou de baixar o wheel
manualmente. `curl`/rede geral funcionam bem (GitHub, NuGet, a maioria dos
sites .gov.br exceto `nfe.fazenda.gov.br` especificamente — cert quebrado,
mesmo memória). Sem GitHub CLI (`gh`) — já registrado na memória global do
usuário (`runner-dev-gh-actions.md`), reconfirmado aqui.

**How to apply:** no início de qualquer sessão nova que dependa de dado
"gerado/baixado antes", checar a existência real do arquivo ANTES de assumir
— e se faltar, ir atrás de fonte pública (GitHub mirrors se dado fiscal/XSD
público) em vez de bloquear esperando a sessão anterior.
