using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Logging;
using StackExchange.Redis;

using Microsoft.AspNetCore.Http.Features;

using Serilog;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Garantir que wwwroot existe (mesmo que vazia) para evitar erro de DirectoryNotFoundException
// Front-end está separado em LayoutParser/wwwroot, mas ASP.NET Core ainda espera a pasta
var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(wwwrootPath))
    Directory.CreateDirectory(wwwrootPath);


// Configurar log por txt (Console.Out/Error) se habilitado
var enableTxtLog = builder.Configuration.GetValue<bool>("Logging:Txt:Enabled", false);
if (enableTxtLog)
{
    var logDirectory = GetLogDirectory(builder.Configuration);
    var logFileName = builder.Configuration["Logging:Txt:FileName"] ?? "log.log";
    var logFilePath = Path.Combine(logDirectory, logFileName);
    
    // Criar diretório se não existir
    if (!Directory.Exists(logDirectory))
        Directory.CreateDirectory(logDirectory);
    
    var fileStream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    var writer = new StreamWriter(fileStream) { AutoFlush = true };
    Console.SetOut(writer);
    Console.SetError(writer);
}

static string GetLogDirectory(IConfiguration configuration)
{
    // Verificar se há pasta customizada configurada
    var customDirectory = configuration["Logging:Txt:CustomDirectory"];
    if (!string.IsNullOrWhiteSpace(customDirectory))
        return customDirectory;
    
    
    // Usar pasta padrão baseada na localização do assembly
    var assemblyLocation = Assembly.GetExecutingAssembly().Location;
    var assemblyDirectory = Path.GetDirectoryName(assemblyLocation) ?? AppDomain.CurrentDomain.BaseDirectory;
    
    // Procurar pela pasta "bin" na árvore de diretórios
    var currentDirectory = new DirectoryInfo(assemblyDirectory);
    while (currentDirectory != null)
    {
        if (currentDirectory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            // Se encontrou "bin", criar "log" na mesma raiz que "bin"
            var parentDirectory = currentDirectory.Parent?.FullName;
            if (parentDirectory != null)
                return Path.Combine(parentDirectory, "log");
        }
        currentDirectory = currentDirectory.Parent;
    }
    
    // Se não encontrou "bin", criar pasta log na mesma raiz que o assembly
    return Path.Combine(assemblyDirectory, "log");
}

var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

// Configurar Serilog baseado no tipo de logging
var loggingType = builder.Configuration["Logging:Type"]?.ToLower() ?? "file";
if (loggingType == "elasticsearch")
    Log.Logger = ElasticSearchLogger.CreateLogger(builder.Configuration, environment);

else
{
    // Padrão: log em arquivo .log na pasta Logs (onde o assembly está rodando)
    var logDirectory = GetSerilogDirectory(builder.Configuration);
    var configuredFileName = builder.Configuration["Logging:File:FileName"] ?? "layoutparserapi.log";
    
    // Criar diretório se não existir
    if (!Directory.Exists(logDirectory))
        Directory.CreateDirectory(logDirectory);
    
    // Construir caminho completo do arquivo de log
    // Serilog com RollingInterval.Day automaticamente adiciona data ao nome do arquivo
    // Se o arquivo contém {Date}, o Serilog substitui; caso contrário, adiciona a data automaticamente
    // Exemplo: "layoutparserapi.log" vira "layoutparserapi20240115.log" ou "layoutparserapi-2024-01-15.log" se usar {Date}
    var logFilePath = Path.Combine(logDirectory, configuredFileName);
    
    Log.Logger = new LoggerConfiguration()
        .WriteTo.File(
            path: logFilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
            shared: true)
        .CreateLogger();
}

static string GetSerilogDirectory(IConfiguration configuration)
{
    // Verificar se há pasta customizada configurada
    var customDirectory = configuration["Logging:File:Directory"];
    if (!string.IsNullOrWhiteSpace(customDirectory))
        return customDirectory;
    
    
    // Usar pasta padrão "Logs" onde o assembly está rodando
    var assemblyLocation = Assembly.GetExecutingAssembly().Location;
    var assemblyDirectory = Path.GetDirectoryName(assemblyLocation) ?? AppDomain.CurrentDomain.BaseDirectory;
    
    return Path.Combine(assemblyDirectory, "Logs");
}

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Configurar serialização JSON para camelCase (compatibilidade com front-end)
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.WriteIndented = false;
    });
builder.Services.AddScoped<ILayoutParserService, LayoutParserService>();
builder.Services.AddScoped<LayoutParserApi.Services.Parsing.Interfaces.ILineSplitter, LayoutParserApi.Services.Parsing.Implementations.LineSplitter>();
builder.Services.AddScoped<LayoutParserApi.Services.Parsing.Interfaces.ILayoutValidator, LayoutParserApi.Services.Parsing.Implementations.LayoutValidator>();
builder.Services.AddScoped<LayoutParserApi.Services.Parsing.Interfaces.ILayoutNormalizer, LayoutParserApi.Services.Parsing.Implementations.LayoutNormalizer>();
builder.Services.AddHttpClient<IIADataGeneratorService, OpenAIDataGeneratorService>();
builder.Services.AddScoped<IIADataGeneratorService, OpenAIDataGeneratorService>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<ITechLogger, TechLogger>();
builder.Services.AddScoped<AuditActionFilter>();
builder.Host.UseSerilog();
builder.Services.AddScoped<LayoutParserApi.Services.Parsing.Interfaces.ILayoutDetector, LayoutParserApi.Services.Parsing.Implementations.LayoutDetector>();

// Serviços de Geração de Dados Sintéticos
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Interfaces.IExcelDataProcessor, LayoutParserApi.Services.Generation.Implementations.ExcelDataProcessor>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Interfaces.ISyntheticDataGeneratorService, LayoutParserApi.Services.Generation.Implementations.SyntheticDataGeneratorService>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Interfaces.ILayoutAnalysisService, LayoutParserApi.Services.Generation.Implementations.LayoutAnalysisService>();
// IAIService removido - usando OllamaAIService diretamente

// Detector de tipo de layout
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Interfaces.ILayoutTypeDetector, LayoutParserApi.Services.Generation.Implementations.LayoutTypeDetector>();

// Validador de linhas incrementais
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Interfaces.ILineValidator, LayoutParserApi.Services.Generation.Implementations.LineValidator>();

// Gerador de valores com regras de consistência
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Interfaces.IValueGenerator, LayoutParserApi.Services.Generation.Implementations.ValueGenerator>();

// Validador de consistência matemática
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Implementations.ConsistencyValidator>();

// Geradores de campos específicos
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Interfaces.IFieldGenerator, LayoutParserApi.Services.Generation.Implementations.FieldGenerators.CommonFieldGenerator>();

// TxtGenerator - Sistema de geração de arquivos .txt
builder.Services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.Parsers.XmlLayoutParser>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.Parsers.ExcelRulesParser>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.Validators.LayoutValidator>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.Generators.DeterministicGenerator>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.Generators.RandomGenerator>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.Generators.SemanticAIGenerator>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.TxtFileGeneratorFactory>();

// Learning Services - Sistema de aprendizado de layout
builder.Services.AddScoped<LayoutParserApi.Services.Learning.FileStorageService>();
builder.Services.AddScoped<LayoutParserApi.Services.Learning.LayoutLearningService>();
builder.Services.AddScoped<LayoutParserApi.Services.Learning.XmlFormatterService>();

// XML Analysis Services - Sistema de análise e validação de XML
builder.Services.AddScoped<LayoutParserApi.Services.XmlAnalysis.XmlAnalysisService>();

// Serviços de IA Online (Gemini) - Solução gratuita e confiável
builder.Services.AddHttpClient<LayoutParserApi.Services.Generation.Implementations.GeminiAIService>();
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Implementations.GeminiAIService>();

// Serviço RAG para consultar exemplos
builder.Services.AddSingleton<LayoutParserApi.Services.Generation.Implementations.RAGService>();

// Serviços de Banco de Dados
builder.Services.AddScoped<LayoutParserApi.Services.Database.IDecryptionService, LayoutParserApi.Services.Database.DecryptionService>();
builder.Services.AddScoped<LayoutParserApi.Services.Database.ILayoutDatabaseService, LayoutParserApi.Services.Database.LayoutDatabaseService>();

// Serviços de Cache Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(provider =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    var connectionString = configuration["Redis:ConnectionString"] ?? "localhost:6379";
    return ConnectionMultiplexer.Connect(connectionString);
});
builder.Services.AddScoped<LayoutParserApi.Services.Cache.ILayoutCacheService, LayoutParserApi.Services.Cache.LayoutCacheService>();

// Serviço com Cache Integrado
builder.Services.AddScoped<LayoutParserApi.Services.Database.ICachedLayoutService, LayoutParserApi.Services.Database.CachedLayoutService>();

// Serviços de Logging Customizados - Removidos por não serem utilizados

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 104857600;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("AllowAll");
// Removido UseStaticFiles() - front-end agora está em LayoutParser/wwwroot (separado do back-end)
app.UseAuthorization();
app.MapControllers();

app.Run();