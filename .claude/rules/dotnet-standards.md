---
description: Padrões de código .NET obrigatórios para o LayoutParser API. Carregar ao editar arquivos .cs.
globs: ["**/*.cs", "Program.cs", "**/*.csproj"]
---

# Padrões .NET — LayoutParser API

Projeto: **ASP.NET Core (.NET 10)**, `Nullable` + `ImplicitUsings` habilitados, `LangVersion: preview`.

## Injeção de dependência

- Registre serviços em `Program.cs` **no grupo já existente** (Cache, Database, XML Analysis, Transformation, Parsing, Mapper Cache, Learning, Validation, Testing, Audit/Logging).
- `Scoped` por padrão. `Singleton` apenas para estado/recurso compartilhado (ex.: `LowCodeTransformationService`, `IConnectionMultiplexer`).
- Serviços com dependência opcional (ex.: Redis) usam *factory lambda* com `sp.GetService<T>()` (nullable), não `GetRequiredService<T>()`.

## Async

- `async/await` ponta a ponta; sufixo **`Async`** em métodos assíncronos.
- **Nunca** `.Result` ou `.Wait()` (deadlock/bloqueio).
- `async void` só em event handlers; nunca em lógica de negócio.

## Resiliência (princípio central do projeto)

Dependências externas — **Redis, SQL Server, Ollama, LayoutParserDecrypt.exe, LowCode runner** — podem falhar.

- Capture, **logue como Warning/Error**, e **degrade**: a app sobe sem Redis; o parse responde mesmo com erro de validação (retorna `validationErrors`).
- Siga o padrão de referência do Redis opcional em `Program.cs` (`try/catch` na conexão, registro condicional).
- Background work (`Task.Run`, `RunInBackgroundAsync`) é *fire-and-forget*: **envolva em try/catch** e **nunca** deixe quebrar a resposta principal.

## Logging

- Use `ILogger<T>` (ou `Serilog.Log` no bootstrap).
- **Mensagens estruturadas:** `_logger.LogInformation("Parse {Layout} ({Type})", name, type)` — não interpolação `$"..."`.
- Preserve o `CorrelationId` (já injetado no `LogContext` pelo middleware).
- **Nunca** logue segredos, tokens ou conteúdo sensível de documentos.

## Serialização JSON

- Mantenha `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` — necessário para preservar XML (`<`, `>`) intacto no payload. **Não troque** por encoder default.
- `DefaultIgnoreCondition = WhenWritingNull`, case-insensitive — já configurado.

## Controllers

- `[ApiController]`, rota `/api/[controller]`.
- Endpoints sensíveis usam `[ServiceFilter(typeof(AuditActionFilter))]`.
- Valide entrada e retorne `BadRequest` com mensagem clara (em PT-BR, como o resto).
- Não coloque lógica de negócio no controller — delegue aos serviços.

## Estilo

- Comentários em **PT-BR**, no tom já presente (inclui os marcadores `// ✅`).
- Nomes: `PascalCase` (tipos/métodos), `camelCase` (locais), `_camelCase` (campos privados), `IFoo` (interfaces).
- Não reintroduza `using` redundante (ImplicitUsings cobre os comuns).

## Antes de concluir

```bash
dotnet build      # obrigatório passar
dotnet test       # quando houver projeto/serviços de teste relevantes
```
