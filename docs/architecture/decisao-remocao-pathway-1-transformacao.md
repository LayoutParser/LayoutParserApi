# Decisão: remoção do Pathway 1 de transformação (legado)

**Data:** 2026-08-12 · **Autor:** `@lp-architect` (Aria) · **Missão:** `review-arch`, issue #41
**Status:** DECIDIDO — remoção aprovada, execução pendente (`@lp-backend-dev`)

## Decisão

Remover o Pathway 1 de transformação: `Controllers/TransformationController.cs`,
`Services/Transformation/MapperTransformationService.cs` e
`Services/Transformation/Interface/IMapperTransformationService.cs`.

Não é mais uma "deprecação futura" — é remoção agora, com evidência de código atual (não a checagem
de 2026-07-21) confirmando ausência de consumidor e ausência de capacidade exclusiva em uso.

## Evidência (grep desta sessão)

| Verificação | Resultado |
|---|---|
| Front-end (`LayoutParserReact/`, incl. BFF `server/`) chama `/api/transformation/*` | Zero ocorrências. Todo código de produção usa `/api/transformationexecution/*` (Pathway 2). |
| MCP Server (`mcp/LayoutParserMcp/`) referencia o Pathway 1 | Só texto de exemplo num tool genérico (`ApiTools.cs:87`), não é chamada real. |
| CI/scripts (`.github/`) chamam o Pathway 1 | Zero ocorrências. |
| `GET available-targets/{inputLayoutGuid}` (única capacidade sem espelho direto no Pathway 2) tem consumidor | Zero — nem front, nem MCP, nem CI. Não é migração pendente, é remoção junto. |

Detalhe completo do raciocínio e do trade-off considerado (manter "por precaução"): ver seção
**"DECISÃO FINAL (2026-08-12)"** em
[`.claude/agent-memory/lp-architect/transformation-pathway-duplication.md`](../../.claude/agent-memory/lp-architect/transformation-pathway-duplication.md).

## Plano de execução (para `@lp-backend-dev`)

1. Remover `Controllers/TransformationController.cs` (inclui o DTO local `TransformRequest`).
2. Remover `Services/Transformation/MapperTransformationService.cs` e
   `Services/Transformation/Interface/IMapperTransformationService.cs`, após confirmar
   (`grep -r "IMapperTransformationService"`) que não há outro consumidor.
3. Remover o registro de DI de `IMapperTransformationService`/`MapperTransformationService` em
   `Program.cs` (grupo Transformation).
4. Checar se `ICachedMapperService.GetMappersByInputLayoutGuidAsync` tem outro consumidor antes de
   tocar — provavelmente permanece (método genérico do cache).
5. `dotnet build` deve compilar limpo (nenhum outro arquivo do repo referencia os símbolos removidos,
   conforme grep desta sessão).
6. `dotnet test` — confirmar se há teste cobrindo o controller removido (não encontrado nesta sessão).
7. Atualizar Swagger/XML docs que citem `/api/transformation/transform` ou `available-targets`
   (`@lp-doc`, se necessário).
8. `@lp-qa` confirma que nenhum quality gate/smoke test aponta para as rotas removidas.

## Rollback

Não é irreversível — se aparecer consumidor real esquecido (script de operação não versionado), o
pathway é recuperável via histórico git.

## Próximo passo

`@lp-pm` comenta a decisão na issue #41 e atualiza o status no Project #2 (esta agente não chama
`gh issue`/`gh project` — autoridade exclusiva do `@lp-pm`).
