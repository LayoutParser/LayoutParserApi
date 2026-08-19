using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// =============================================================================
// LayoutParser MCP Server (stdio)
//
// Servidor MCP que expõe operações do LayoutParser API como *tools* para agentes.
// É um CLIENTE FINO sobre a API HTTP — a API continua sendo a fonte da verdade.
//
// IMPORTANTE (protocolo stdio): a comunicação MCP usa STDOUT. Todo log DEVE ir
// para STDERR, senão corrompe o protocolo. Por isso o sink de console do Serilog
// abaixo é forçado para stderr (standardErrorFromLevel: LogEventLevel.Verbose).
//
// Configuração via env var:
//   LAYOUTPARSER_API_URL   base da API (default http://localhost:5000)
//   LAYOUTPARSER_LOG_DIR   diretório do arquivo de log (default "Logs" relativo ao cwd do MCP)
// =============================================================================

var builder = Host.CreateApplicationBuilder(args);

// ✅ Mesmo padrão de logging da API (ver Program.cs da API): Serilog com outputTemplate
// idêntico e Source fixo ("MCP", análogo a "Backend"/"Frontend"), pra que os 3 arquivos
// fiquem correlacionáveis pelo mesmo formato de linha (UnifiedLogReaderService já parseia
// esse formato via ApiLinePattern).
var logDirectory = Environment.GetEnvironmentVariable("LAYOUTPARSER_LOG_DIR")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "Logs");

try
{
    if (!Directory.Exists(logDirectory))
        Directory.CreateDirectory(logDirectory);
}
catch (Exception ex)
{
    // Não pode derrubar o MCP por falha ao criar o diretório de log — cai pro cwd atual
    // (Serilog.Sinks.File também cria o diretório sozinho, isso aqui é só best-effort de log).
    Console.Error.WriteLine($"[BOOTSTRAP] WARNING: falha ao criar diretório de log '{logDirectory}': {ex.Message}");
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Source", "MCP")
    // stdout é o canal do protocolo MCP — forçar TODO log de console pra stderr, não só acima
    // de um nível mínimo.
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Corr:{CorrelationId}] [Src:{Source}] {Message:lj}{NewLine}{Exception}",
        standardErrorFromLevel: LogEventLevel.Verbose)
    .WriteTo.File(
        Path.Combine(logDirectory, "layoutparsermcp.log"),
        rollingInterval: RollingInterval.Day,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Corr:{CorrelationId}] [Src:{Source}] {Message:lj}{NewLine}{Exception}",
        shared: true)
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Services.AddSerilog();

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

Log.Information("LayoutParser MCP Server iniciando. API base: {ApiBaseUrl}. Log directory: {LogDirectory}", apiBaseUrl, logDirectory);

try
{
    await builder.Build().RunAsync();
}
finally
{
    Log.CloseAndFlush();
}
