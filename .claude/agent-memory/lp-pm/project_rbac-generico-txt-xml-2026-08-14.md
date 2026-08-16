---
name: project-rbac-generico-txt-xml-2026-08-14
description: Lote de 7 issues (#92-#98) do desenho de RBAC/escopo genérico TXT-XML de @lp-architect (2026-08-14) — ordem de bloqueio e status
metadata:
  type: project
---

Doc de origem `docs/architecture/escopo-generico-txt-xml-e-acesso-por-papel-2026-08-14.md`
existe só na branch `fix/auditoria-gates-2026-08-14` (commit `9494041`), **não mergeada** em
`develop` no momento em que as issues foram criadas (2026-08-15). Se o doc não aparecer no
checkout atual, buscar via `git show <branch>:<path>` ou pelo commit.

Issues criadas, nesta ordem (a #92 foi criada primeiro de propósito, por ser o bloqueio):

- **#92** (bug) — `AiCandidateStore` sem particionamento por usuário → **bloqueia #93**.
- **#93** (story) — abrir `execute-candidates`/`ia-status`/`execute-lowcode` para qualquer
  usuário autenticado (reverte parcial da #32) → **bloqueada por #92**.
- **#94** (story) — governança de mapeadores pelo admin (CRUD/promoção TCL/XSL) — dono
  `@lp-architect` primeiro (é design, não implementação ainda).
- **#95** (security) — `GET export/{id}` do `MapperDatabaseController` sem `[Authorize]` nenhum,
  devolve `DecryptedContent`.
- **#96** (tech-debt) — `FindXslFile` recebe `sourceType`/`targetType` mas só usa no log, não na
  busca real (`Directory.GetFiles` usa só `layoutName`). Relacionada à #55 (já fechada, resolveu
  só a convenção de nome, não este gap).
- **#97** (story, longo prazo) — sessão de IA persistente por usuário, fase 2 além do
  particionamento básico da #92.
- **#98** (story) — prompt customizado do usuário, complementar (não substitui) o prompt padrão
  do pathway IA em `AiTransformationCandidateService.BuildPrompt`.

**Item 8 do pedido original não virou issue** (por instrução explícita) — é pergunta de produto
sobre login com conta pessoal Google/Microsoft acessando dado fiscal, fora do escopo deste repo
(é decisão do dono / `LayoutParserReact`/BFF). Só reportado em texto, sem `gh issue create`.

O bloqueio #92→#93 foi registrado via `gh issue comment` no #92 (não há campo nativo de
"blocked-by" usado aqui — a convenção deste projeto é texto no corpo + comentário cruzado).

Related: [[reference-gh-cli-setup]]
