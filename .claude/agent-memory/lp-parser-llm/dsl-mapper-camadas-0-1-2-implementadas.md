---
name: dsl-mapper-camadas-0-1-2-implementadas
description: Implementação real das Camadas 0/1/2 do design-dsl-mapper-prompt-ia-2026-08-16.md — parser DSL→JSON, schema, catálogo de funções ofuscado.
metadata:
  type: project
---

Branch `feat/dsl-mapper-contexto-ia` (commit `8c6c2e0`, local — não pushado, `@lp-devops`
mergeia em `develop`) implementa o design fechado em
`docs/architecture/design-dsl-mapper-prompt-ia-2026-08-16.md`.

**Camada 0** — `ai/XslSynth.Core/Core/DslStructuredParser.cs`: tokenizer + parser
recursivo-descendente REAL da gramática Sysmiddle (não regex incremental como o
`DslBlockInterpreter` existente, que só cobre guarded-emit). Cobre `if/else` com
`begin/end`, condição composta `&&`, chamadas de função bare (`Nome(args)` = `F.Nome`),
resolve origens `I.` transitivamente através de `#./$.` — inclusive quando o temp é
calculado dentro de um if/else que não emite `T.` em nenhum ramo (ex.: fallback de data
`#.AAMM` dentro da regra da chave de acesso): nesse caso as duas alternativas são
mescladas via `DslBinary("||", ...)` só para não perder nenhuma origem possível, já que
não dá pra saber em parse-time qual ramo roda. Nunca lança para DSL real.

**Camada 1** — `ai/XslSynth.Core/Prompting/StructuredRuleSchema.cs`: records
`StructuredRule`/`StructuredBranch`, `SchemaVersion` const para bump futuro de forma.

**Camada 2** — `ai/XslSynth.Core/Prompting/FunctionCatalog.cs`: extração via
`MetadataLoadContext` (reflection-only, NÃO executa a DLL) de
`SysMiddle.ConnectUs.Functions.dll`. Achado confirmado por decompilação (`ilspycmd`,
2026-08-16) contra `.claude/tmp/sysmiddle/` (essa pasta só existe na raiz do repo
principal, NÃO em worktrees — se for reabrir esse teste em worktree novo, o caminho
`C:\Users\elson.lopes\source\repos\LayoutParserApi\.claude\tmp\sysmiddle\...` ainda
funciona via path absoluto cross-worktree, é só leitura): a DLL usa control-flow
flattening + strings criptografadas em runtime — `Name`/`OwnerName` sempre decompilam
como `return null`, `Execute(object[])` é uniforme sem tipos reais de parâmetro. Extraí o
que É seguro (nome de classe tipo `ConcatFunction`, namespace, herança confirmada de
`SysMiddle.Base.Function.FunctionMember`) e marquei `NameIsReliable=false` em toda
entrada — é candidato, não confirmação. NÃO tentei deobfuscation (fora de escopo,
autorização do dono foi só pra `ilspycmd`/reflection de assinatura pública, não pra
decompilar lógica interna).

**FewShotIndex.RetrieveStructured** (`ai/XslSynth.Core/Synthesis/FewShotIndex.cs`):
novo método de recuperação por JSON estruturado (sobreposição de nomes de função + forma
da árvore de ramos + leaf do target), complementar ao `Retrieve` existente por regex/
`DslTraits`. `IndexaMapper` agora popula `FewShotExample.Structured` via
`DslStructuredParser` best-effort (try/catch, não quebra indexação).

**Testes**: `ai/XslSynth.Core.Tests` (projeto novo, standalone, não entra na
`LayoutParserApi.sln` — mesmo raciocínio do `XslSynth.Core`; rodar com
`dotnet test ai/XslSynth.Core.Tests/XslSynth.Core.Tests.csproj`). 11 casos, todos verdes.
Usa as 2 regras reais de `story/103` (chave de acesso completa + trecho real de
Regra_ICMSTotal) copiadas literal como strings — não depende de arquivo externo, roda em
qualquer worktree/CI. O teste de `FunctionCatalog` contra a DLL real (`Dll_real_extrai_...`)
faz early-return se o arquivo não existir na máquina — documental, não quebra CI onde a
DLL não está disponível.

Gotcha de sessão: `.claude/tmp/` (story/103, sysmiddle DLLs) só existe no checkout
principal do repo, cada worktree novo nasce sem ele — path absoluto cross-worktree
funciona para leitura (Bash não bloqueia isso, só bloqueia `cd` complexo cruzando
fronteira em operações que pareçam git). Ver também [[dsl-mapper-proximos-passos]].

Próximo passo (não fechado nesta sessão): `@lp-qa` valida, `@lp-devops` mergeia em
`develop` quando verde. Diff completo: 9 arquivos, +1095/-2 — 3 arquivos novos em
`ai/XslSynth.Core.Tests/` + 3 em `ai/XslSynth.Core/{Core,Prompting}/` + `FewShotIndex.cs`
e `XslSynth.Core.csproj` modificados (novo `PackageReference
System.Reflection.MetadataLoadContext`).
