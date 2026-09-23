---
name: issue140-resolucao-estrutural-qa-gate
description: QA gate da issue #140 (motor de resolução estrutural TXT->XML) — PASS estrutural; validação comportamental de 20 execuções contra LowCodeRunner real é IMPOSSÍVEL neste ambiente Linux e fica pendente para o dono
metadata:
  type: project
---

Commits `36ae5cb` (motor) e `9b2d0d0` (wiring HTTP) na branch `feat/resolucao-estrutural-txt-xml-140`.
`dotnet build` limpo (0 erros, warnings pré-existentes só). `dotnet test`: XslSynth.Core.Tests
59/59 (36 pré-existentes + 23 novos da matriz de 20 cenários), LayoutParserApi.Tests 397/401
(4 falhas pré-existentes, Windows path `C:\...` vs Linux — mesma baseline documentada em
[[informacoesparaedi-occurrencecount-fix-qa-gate]] e memórias anteriores, não é regressão desta
issue).

**Por que a validação comportamental de 20 execuções contra o `.exe` real é impossível aqui:**
`LayoutParserLowCodeRunner.exe` é .NET Framework 4.8.1 x86, Windows-only, com interop nativo
Sysmiddle (ver `.claude/agent-memory/lp-architect/...` sobre a migração Linux+Ollama) — não roda em
WSL/Linux. Não há caminho de contorno (Wine/emulação não testado nem confiável para interop nativo).
Único caminho real: o dono rodar num ambiente Windows com o runner instalado, ou um agente futuro
com acesso a esse ambiente.

**O que foi feito em vez disso (Parte 2 da tarefa):** construí
`ai/XslSynth.Core.Tests/StructuralResolution/Issue140TwentyScenarioMatrixTests.cs` — 20 linhas do
design §6.1 cobertas por 23 testes determinísticos contra o composer/classificador reais (fixtures
sintéticas, sem `.exe`, sem dado real de cliente), comparando XPath/ocorrência previstos contra o
que é estruturalmente correto dado mapper+layout sintéticos — não contra saída real do runner
(oráculo diferente do pedido original, documentado explicitamente nos comentários do arquivo).

**Achado 1 — linhas 1-3 da matriz (TXT/MQSeries/IDOC) colapsam num único teste:** o composer é
agnóstico ao tipo de layout de origem — `TxtFieldReference`/`MappingCandidate` não carregam nenhum
campo que distinga os 3 formatos; a diferenciação acontece inteiramente antes do composer, na
camada de parsing (fora do escopo #140). Não é lacuna, é achado estrutural correto — documentado no
teste `Linhas01a03_TipoDeLayoutDeOrigem_ComposerEhAgnostico`.

**Achado 2 — GAP REAL confirmado (linhas 9 e 20 da matriz):** `LineInfo.IsDeclaredEmpty` e
`LineInfo.PositionalAlignmentFailed` (contrato de degradação posicional, 2026-08-27) NÃO chegam ao
motor — `FieldMappingCompositionService.Compose(Layout, IReadOnlyList<ParsedField>, MapperVo)` não
recebe `LineInfo` nenhum, e `MappingCandidate` não tem campo equivalente. Confirmado por
`grep -rn "IsDeclaredEmpty|PositionalAlignmentFailed"` em `ai/` e
`Services/Transformation/StructuralResolution/` — zero ocorrências. Consequência: um mapeamento
vindo de uma linha declarada vazia ou com alinhamento posicional degradado pode sair
`Authoritative` hoje, mesmo que o design (linha 9/20 de §6.1) esperasse degradação automática para
`best-effort`. A parte "nunca lança exceção" está OK (confirmado nos testes `Linha09_.../Linha20_...`
— são os dois únicos testes da matriz que documentam o gap em vez de mascará-lo). Ação: devolver a
`@lp-backend-dev`/`@lp-parser-llm` — precisa de um 6º sinal no critério §5 do design, ou
`MappingCandidate` ganhar `SourceIsDeclaredEmpty`/`SourceHasPositionalAlignmentFailure` e o
composer tratar qualquer um dos dois como automaticamente `best-effort`.

**Achado 3 — linha 19 (Elements aninhados no MapperVO) não é testável no nível do composer:** é
limitação do parser do MapperVO (#139 §7.1), uma camada anterior — um teste no composer só provaria
que ele funciona com o candidate que o parser já produziu, não exercitaria a limitação real (parser
não produzir candidate nenhum, silenciosamente). Documentado como gap de cobertura explícito, não
mascarado com um teste que não prova nada.

**Confirmação pedida pela tarefa (item 4):** o critério §5 já é conservador — teste
`NuncaAuthoritative_QuandoFunctionCatalogIndisponivel_MesmoSemFuncoesReferenciadas` prova que
`KnownFunctions == null` (estado real de `FieldMappingCompositionService` hoje — nenhum host
configura `FunctionCatalog`) sempre degrada para `best-effort`, mesmo sem nenhuma função
referenciada (a checagem é `KnownFunctions is not null &&`, não vacuidade de `.All()` numa lista
vazia).

**Veredito:** PASS estrutural (build limpo, sem regressão, critério §5 objetivo e conservador
confirmado por teste, degradação graciosa confirmada). CONCERNS na validação comportamental exigida
pelo critério de aceite original — pendente, só o dono (ambiente Windows) pode fechar — e nos gaps 2
e 3 acima, que exigem trabalho de código antes de qualquer mapeamento poder ser tratado como
confiável em produção para linhas vazias/degradadas.
