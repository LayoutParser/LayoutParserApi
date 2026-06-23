---
description: Cria um novo endpoint seguindo os padrões do projeto (controller + service + DI).
argument-hint: <descrição do endpoint, ex.: "GET layouts por tipo de documento">
allowed-tools: Read, Grep, Glob, Edit, Write, Bash
---

# /new-endpoint

Implemente um novo endpoint para: **$ARGUMENTS**

## Antes de escrever (protocolo IDS)

1. `Grep`/`Glob` por controller e serviço semelhantes — **reusar/adaptar** antes de criar.
2. Leia [`.claude/rules/dotnet-standards.md`](../rules/dotnet-standards.md).
3. Confirme o grupo de DI correto em `Program.cs`.

## Implementação

1. **Controller** em `Controllers/`: `[ApiController]`, rota `/api/[controller]`, validação de entrada com `BadRequest` claro (PT-BR). Endpoint sensível → `[ServiceFilter(typeof(AuditActionFilter))]`.
2. **Serviço + interface** em `Services/<área>/` — toda a lógica vive aqui, não no controller.
3. **DTOs** em `Models/` (request/response).
4. **Registre** o serviço em `Program.cs` no grupo certo (`Scoped` por padrão).
5. **Async** ponta a ponta; **degrade graciosamente** se depender de Redis/SQL/Ollama.
6. `ILogger<T>` estruturado, preservando `CorrelationId`.

## Concluir

- `dotnet build` deve passar — reporte o resultado fielmente.
- Acione `@lp-doc` para Swagger/README e `@lp-qa` para teste.
- Liste arquivos criados/alterados.
