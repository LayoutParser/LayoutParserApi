# LayoutParser MCP Server

> **PT-BR** · Servidor **MCP (Model Context Protocol)** em **C# / .NET 10** que expõe as
> operações do LayoutParser API como *tools* para agentes de IA. É um **cliente fino sobre
> a API HTTP** — a API continua sendo a fonte da verdade.
>
> **EN** · A **C# / .NET 10 MCP server** exposing the LayoutParser API operations as agent
> *tools*. It is a **thin client over the HTTP API** — the API remains the source of truth.

## Arquitetura

```
Agente/LLM  ──(MCP stdio)──►  LayoutParserMcp (C#)  ──(HTTP)──►  LayoutParserApi  ──►  Redis / SQL / Ollama
```

A lógica de negócio **não** é duplicada aqui; cada tool apenas chama um endpoint da API.

## Tools disponíveis

| Tool | O que faz |
|------|-----------|
| `parse_document` | Parseia um documento (TXT/MQSeries/IDOC) contra um layout XML → estrutura JSON. (`POST /api/parse/upload`) |
| `list_endpoints` | Lista os endpoints da API a partir do Swagger/OpenAPI em runtime. |
| `api_get` | GET genérico em qualquer caminho da API (ex.: `/api/LayoutDatabase`). |
| `api_post` | POST genérico (corpo JSON) em qualquer caminho da API. |

> `list_endpoints` + `api_get/api_post` permitem ao agente **descobrir e chamar qualquer
> endpoint** sem hardcodar rotas — robusto a uma API em evolução. Conforme as rotas se
> estabilizam, adicione tools tipadas dedicadas (siga o padrão de `ParseTools`).

## Configuração

| Env var | Default | Descrição |
|---------|---------|-----------|
| `LAYOUTPARSER_API_URL` | `http://localhost:5000` | Base URL da API. |

## Build & run

```bash
cd mcp/LayoutParserMcp
dotnet restore
dotnet build -c Release
```

> ⚠️ **stdio:** o protocolo MCP usa **stdout**. Por isso o servidor loga em **stderr**
> (configurado em `Program.cs`). **Não** use `dotnet run` no registro do MCP: o build
> imprime em stdout e corrompe o protocolo. Registre apontando para a **DLL compilada**.

Teste rápido (o processo fica aguardando mensagens MCP no stdin — Ctrl+C para sair):

```bash
LAYOUTPARSER_API_URL=http://localhost:5000 dotnet bin/Release/net10.0/LayoutParserMcp.dll
```

## Registrar no Claude Code

Copie [`../../.mcp.json.example`](../../.mcp.json.example) para `.mcp.json` na raiz do repo
(ou use `claude mcp add`). Ajuste o caminho da DLL e a URL da API. Exemplo:

```json
{
  "mcpServers": {
    "layoutparser": {
      "command": "dotnet",
      "args": ["mcp/LayoutParserMcp/bin/Release/net10.0/LayoutParserMcp.dll"],
      "env": { "LAYOUTPARSER_API_URL": "http://localhost:5000" }
    }
  }
}
```

> A gestão de MCP (registro, build, config) é responsabilidade do agente **@lp-devops**
> — ver [`.claude/rules/mcp-usage.md`](../../.claude/rules/mcp-usage.md).

## Estrutura

```
mcp/LayoutParserMcp/
├── LayoutParserMcp.csproj   # net10.0, ModelContextProtocol 1.0.0
├── Program.cs               # host stdio + HttpClient("api") + log em stderr
├── Tools/
│   ├── ParseTools.cs        # parse_document
│   └── ApiTools.cs          # list_endpoints, api_get, api_post
└── README.md
```

## Como adicionar uma tool tipada

1. Crie um método em uma classe `[McpServerToolType]`.
2. Anote com `[McpServerTool(Name = "...")]` + `[Description("PT / EN")]`.
3. Injete `IHttpClientFactory` e parâmetros com `[Description]`.
4. Chame o endpoint da API e retorne a resposta (string/JSON).
5. `WithToolsFromAssembly()` descobre a tool automaticamente — sem registro manual.
