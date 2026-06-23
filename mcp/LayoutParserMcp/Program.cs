using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// =============================================================================
// LayoutParser MCP Server (stdio)
//
// Servidor MCP que expõe operações do LayoutParser API como *tools* para agentes.
// É um CLIENTE FINO sobre a API HTTP — a API continua sendo a fonte da verdade.
//
// IMPORTANTE (protocolo stdio): a comunicação MCP usa STDOUT. Todo log DEVE ir
// para STDERR, senão corrompe o protocolo. Por isso o console loga em stderr.
//
// Configuração via env var:
//   LAYOUTPARSER_API_URL   base da API (default http://localhost:5000)
// =============================================================================

var builder = Host.CreateApplicationBuilder(args);

// Logs SEMPRE em stderr (stdout é o canal do protocolo MCP).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// HttpClient nomeado apontando para a API.
var apiBaseUrl = Environment.GetEnvironmentVariable("LAYOUTPARSER_API_URL") ?? "http://localhost:5000";
builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(120);
});

// Registra o servidor MCP via stdio e descobre as tools no assembly ([McpServerToolType]).
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
