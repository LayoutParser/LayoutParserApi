using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Logging;

using Microsoft.AspNetCore.Http.Features;

using Serilog;

using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

// Configurar logging baseado na configuração
var loggingStrategy = LoggingStrategyFactory.CreateStrategy(builder.Configuration, environment);
var configurableLogger = new ConfigurableLogger(loggingStrategy);

// Configurar Serilog baseado no tipo de logging
var loggingType = builder.Configuration["Logging:Type"]?.ToLower() ?? "file";
if (loggingType == "elasticsearch")
{
    Log.Logger = ElasticSearchLogger.CreateLogger(builder.Configuration, environment);
}
else
{
    // Para file e console, usar configuração básica do Serilog
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .CreateLogger();
}

builder.Services.AddControllers();
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
builder.Services.AddScoped<LayoutParserApi.Services.Generation.Interfaces.IAIService, LayoutParserApi.Services.Generation.Implementations.AIService>();

// Serviços de Logging Customizados
builder.Services.AddSingleton(configurableLogger);
builder.Services.AddScoped<IDataGenerationLogger, DataGenerationLogger>();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 104857600;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();

app.Run();