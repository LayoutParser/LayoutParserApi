# Decisão — mecanismo real da DSL do Mapper Sysmiddle (2026-08-21)

> **PT-BR.** Fecha a pergunta aberta desde `dsl-mapper-roslyn-hypothesis-2026-08-16.md`.
> Autoria: `@lp-architect`. Execução: `@lp-parser-llm`. Decompilação autorizada pelo dono
> nesta sessão, via `ilspycmd`, sobre `tools/LowCodeRunner/Functions/*.dll` (binários já
> versionados no repo como dependência de runtime, não baixados de fora).

## 1. O que a DSL realmente é

`SysMiddle.Map4Connect.Rule.dll` (não ofuscada, decompila com fidelidade total) contém a
classe `RuleInterpretor` (`SysMiddle.Map4Connect.Rule.Service`). Ela é um **interpretador
proprietário escrito à mão, line-based**, não um compilador para C#/IL e não usa Roslyn,
CodeDom, nem nenhum motor de script de terceiro (nenhuma referência a
`Microsoft.CodeAnalysis` em nenhuma DLL do conjunto — achado de 2026-08-16 permanece válido
e agora está confirmado pelo código-fonte real, não só por ausência de evidência).

Mecanismo, em resumo:
- `Process()` recebe `RuleVO.ContentValue` (o texto bruto salvo no XML do mapper) e lê linha
  a linha via `StringReader` (`GetLines`).
- O texto precisa começar com o marcador literal `%beginRuleContent;` e terminar com
  `%endRuleContent;` — sentinelas do próprio formato, não delimitadores de linguagem
  genérica.
- `GetBlocks`/`SetBlockChildren` constroem uma árvore de `CodeBlock` reconhecendo os tokens
  literais `begin`/`end` como abertura/fechamento de bloco após uma linha `if(`/`else
  if(`/`else` (dicionário `_validBlocks` com essas três formas hard-coded) — explica por que
  a amostra usa `begin/end` em vez de `{ }`: é a sintaxe real, não uma etapa intermediária.
- Comparações usam `=`/`!=` sobre `.ToString()` dos valores resolvidos (`resultCondition`),
  não `==` de C# — confirma que `if(#.x = 44)` já é a gramática literal do motor, não uma
  tradução prévia.
- Funções (`GetLength()` etc., citadas em `ia-xslt-synthesis.md`) são despachadas por
  `ExecuteRuleFunction`/`ExecuteGetValueFromContext`/`ExecuteGetDictionaryValuesFromElement`/
  `ExecuteGetSumElementValuesFunction` — um dispatcher interno por nome de função conhecida,
  não reflexão genérica sobre uma API pública.

**Refuta definitivamente a hipótese de Roslyn** (2026-08-16) e fecha a hipótese (A) do gap
de "Functions" levantado em `viabilidade-dlls-sysmiddle-para-rag.md` §4: são chamadas
internas ao próprio `ContentValue`, resolvidas por um dispatcher fechado do motor — não um
artefato externo separado a indexar.

## 2. Decisão de arquitetura para o `RealMapperParser`

**Não usar `Microsoft.CodeAnalysis.CSharp` nem qualquer parser de linguagem geral.**
`RealMapperParser`/`DslBlockInterpreter` devem continuar como **parser dedicado da gramática
Sysmiddle**, e agora podem ser desenhados com precisão em vez de heurística, porque a
gramática real é pequena e fechada:

1. **Tokenizer de linha**, não de caractere: cada linha relevante do `ContentValue` é uma
   unidade (`StringReader.ReadLine()` + `Trim()`), igual ao original.
2. **Sentinelas fixas**: reconhecer `%beginRuleContent;`/`%endRuleContent;` como delimitadores
   obrigatórios do bloco de regra — descartar tudo fora deles.
3. **Três formas de condicional, hard-coded**: `if(`, `else if(`, `else` — não generalizar
   para uma gramática de expressão arbitrária. Blocos abrem com a linha seguinte iniciando em
   `begin` e fecham em `end`.
4. **Operador de igualdade é `=`/`!=` sobre string**, não `==`/`!=` de C# — ao gerar
   XSLT/relatório de mapeamento, traduzir literalmente para o equivalente XPath (`=`/`!=`
   já coincide, por sorte, com a sintaxe XSLT/XPath nativa).
5. **Catálogo fechado de funções**: espelhar o dispatcher (`GetValueFromContext`,
   `GetDictionaryValuesFromElement`, `GetSumElementValuesFunction`, + funções nomeadas via
   `RuleFunctionVO`) como uma tabela conhecida de tradução DSL→XSLT/XPath, em vez de tentar
   interpretar chamadas de função como se fossem extensíveis livremente.

**Não commitar código decompilado no repo** (risco de licença — Sysmiddle Technology é
fornecedor terceiro). Este documento descreve o mecanismo em prosa; a saída do `ilspycmd`
usada nesta investigação ficou fora do controle de versão, em `.claude/tmp/ilspy_out/`
(git-ignorado).

## 3. Próximo passo

`@lp-parser-llm` implementa/ajusta `RealMapperParser` conforme §2, com testes cobrindo os 3
casos de condicional + o operador `=`/`!=` + pelo menos as 4 funções internas confirmadas
acima antes de expandir para outras.
