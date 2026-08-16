---
name: dsl-mapper-roslyn-hypothesis-2026-08-16
description: ContentValue do Mapper usa "begin/end" e "=" de comparação simples — não é C# válido literal; hipótese Roslyn do dono precisa confirmação antes de trocar o parser
metadata:
  type: project
---

Dono levantou hipótese: o low-code Sysmiddle interpreta `if/else/for/foreach/while` do
`ContentValue` via Roslyn Scripting (`Microsoft.CodeAnalysis.CSharp.Scripting`), o que
sugeriria usar `SyntaxTree`/`SemanticModel` real em vez do parser regex/heurístico atual
(`RealMapperParser`) para a Fase 1-2 do plano de mapeamento campo TXT↔XML.

A amostra real (`.claude/tmp/story/103/MAP_f31a...xml`, regra `Regra_chaveDeAcesso`)
contradiz isso sintaticamente: usa `if(#.x = 44)` (um `=`, não `==`) e `begin...end` em
vez de `{...}` — não compila como C#. Duas hipóteses ficaram em aberto em
`docs/architecture/design-dsl-mapper-prompt-ia-2026-08-16.md` §1.1: (A) existe uma
tradução DSL→C# antes do Roslyn (Roslyn nunca vê o `ContentValue` bruto), ou (B) o texto
salvo já é uma etapa intermediária e o Roslyn real atua sobre outro artefato que não
vimos.

**Why:** decide se vale reescrever `DslBlockInterpreter`/`RealMapperParser` em cima de
`Microsoft.CodeAnalysis.CSharp` (só funciona se o texto for C# válido) ou manter um
parser dedicado da gramática Sysmiddle — trocar sem confirmar arrisca queimar trabalho
numa hipótese que a própria amostra já contradiz sintaticamente.

**How to apply:** antes de a Lia/Dex tocarem `DslBlockInterpreter` para adotar Roslyn,
essa pergunta específica precisa ir ao dono via coordenador: "o `=` simples e o
`begin/end` do `ContentValue` são a sintaxe literal que o Sysmiddle roda (com um
parser/tradutor próprio antes do Roslyn), ou existe uma etapa de tradução que não
aparece nesta amostra?" Enquanto não respondida, manter o parser dedicado como aposta
segura — o achado ainda vale (confirma que é gramática determinística, não freestyle),
só não decide a ferramenta de parsing ainda.

Ver também [[track-a2-a5-spec]] (Fase 1-2 do plano de mapeamento, onde essa decisão
encaixa).
