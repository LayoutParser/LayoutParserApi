using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Testing;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Transformation.Interface;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Diagnostics;

using Serilog;
using Serilog.Events;

using StackExchange.Redis;

using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

// Bootstrap logger for errors before Serilog is configured
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure Serilog for file and console logging
    var logDirectory = builder.Configuration["Logging:File:Directory"] ?? "Logs";
    var logFileName = builder.Configuration["Logging:File:FileName"] ?? "layoutparserapi.log";

    // Ensure log directory exists and is writable
    try
    {
        Console.WriteLine($"[BOOTSTRAP] Configuring log directory: {logDirectory}");
        Console.WriteLine($"[BOOTSTRAP] Current working directory: {Directory.GetCurrentDirectory()}");

        if (string.IsNullOrEmpty(logDirectory))
        {
            logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Console.WriteLine($"[BOOTSTRAP] Log directory was empty, using default: {logDirectory}");
        }

        if (!Directory.Exists(logDirectory))
        {
            Console.WriteLine($"[BOOTSTRAP] Creating log directory: {logDirectory}");
            Directory.CreateDirectory(logDirectory);
            Console.WriteLine($"[BOOTSTRAP] Log directory created successfully");
        }
        else
        {
            Console.WriteLine($"[BOOTSTRAP] Log directory already exists: {logDirectory}");
        }

        // Test write permissions
        var testFile = Path.Combine(logDirectory, "test-write-permissions.tmp");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            Console.WriteLine($"[BOOTSTRAP] Log directory is writable: {logDirectory}");
        }
        catch (Exception writeEx)
        {
            Console.WriteLine($"[BOOTSTRAP] WARNING: Cannot write to log directory '{logDirectory}': {writeEx.Message}");
            // Fallback to current directory
            logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
            Console.WriteLine($"[BOOTSTRAP] Using fallback log directory: {logDirectory}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[BOOTSTRAP] ERROR: Failed to setup log directory '{logDirectory}': {ex.Message}");
        Console.WriteLine($"[BOOTSTRAP] Stack trace: {ex.StackTrace}");
        // Fallback to current directory
        logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
        try
        {
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
            Console.WriteLine($"[BOOTSTRAP] Using fallback log directory: {logDirectory}");
        }
        catch
        {
            Console.WriteLine($"[BOOTSTRAP] CRITICAL: Cannot create fallback log directory. Logging may not work.");
        }
    }

    // Configure Serilog with error handling
    try
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDirectory, logFileName.Replace(".log", "-{Date}.log")),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                shared: true)
            .CreateLogger();

        builder.Host.UseSerilog();
        Log.Information("Serilog configured successfully. Log directory: {LogDirectory}", logDirectory);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[BOOTSTRAP] ERROR: Failed to configure Serilog: {ex.Message}");
        Console.WriteLine($"[BOOTSTRAP] Stack trace: {ex.StackTrace}");
        // Continue with default logging
    }

    Log.Information("Starting LayoutParserApi initialization...");

    // Add services to the container.
    // Configurar opções JSON para não escapar caracteres XML/HTML
    // Isso preserva o XML intacto no JSON (não converte < para \u003C, etc.)
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            options.JsonSerializerOptions.WriteIndented = false;
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // CORS Configuration
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(
                    "http://172.25.32.42:81",
                    "http://localhost:81",
                    "http://localhost:8080",
                    "http://172.25.32.42:80",
                    "http://localhost:80",
                    "http://127.0.0.1:81",
                    "http://127.0.0.1:8080"
                  )
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .WithExposedHeaders("*");
        });
    });

    // Redis Configuration - Make it optional to prevent startup failure
    var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
    Log.Information("Configuring Redis connection at {RedisConnection}", redisConnectionString);

    // Try to connect to Redis, but don't fail if it's not available
    IConnectionMultiplexer? redisConnection = null;
    try
    {
        redisConnection = ConnectionMultiplexer.Connect(redisConnectionString);
        Log.Information("Successfully connected to Redis at {RedisConnection}", redisConnectionString);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to connect to Redis at {RedisConnection}. The application will start but caching features will be disabled.", redisConnectionString);
        redisConnection = null;
    }

    // Register Redis connection - make it optional for services
    if (redisConnection != null)
    {
        builder.Services.AddSingleton<IConnectionMultiplexer>(redisConnection);
        Log.Information("Redis service registered successfully");
    }
    else
    {
        Log.Warning("Redis is not available. Cache services will operate without Redis.");
    }

    // Cache Services - will handle null Redis connection gracefully
    builder.Services.AddScoped<ILayoutCacheService>(sp =>
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var logger = sp.GetRequiredService<ILogger<LayoutCacheService>>();
        return new LayoutCacheService(redis, logger);
    });

    // Database Services
    builder.Services.AddScoped<ILayoutDatabaseService, LayoutDatabaseService>();
    builder.Services.AddScoped<IDecryptionService, DecryptionService>();
    builder.Services.AddScoped<MapperDatabaseService>();
    builder.Services.AddScoped<ICachedLayoutService, CachedLayoutService>();

    // XML Analysis Services
    builder.Services.AddScoped<XmlAnalysisService>();
    builder.Services.AddScoped<XsdValidationService>();
    builder.Services.AddScoped<XmlDocumentTypeDetector>();
    builder.Services.AddScoped<MqSeriesToXmlTransformer>();
    builder.Services.AddScoped<TransformationPipelineService>();
    builder.Services.AddScoped<TclGeneratorService>();
    builder.Services.AddScoped<XslGeneratorService>();
    builder.Services.AddScoped<AutoTransformationGeneratorService>();

    // Transformation Services (ML)
    builder.Services.AddScoped<TransformationLearningService>();
    builder.Services.AddScoped<PatternComparisonService>();
    builder.Services.AddScoped<ImprovedTclGeneratorService>();
    builder.Services.AddScoped<ImprovedXslGeneratorService>();
    builder.Services.AddScoped<TransformationValidatorService>();

    // Parsing Services
    builder.Services.AddScoped<ILineSplitter, LineSplitter>();
    builder.Services.AddScoped<ILayoutValidator, LayoutValidator>();
    builder.Services.AddScoped<ILayoutNormalizer, LayoutNormalizer>();
    builder.Services.AddScoped<ILayoutDetector, LayoutDetector>();
    builder.Services.AddScoped<ILayoutParserService, LayoutParserService>();

    // Mapper Cache Services
    builder.Services.AddScoped<IMapperCacheService>(sp =>
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var logger = sp.GetRequiredService<ILogger<MapperCacheService>>();
        return new MapperCacheService(redis, logger);
    });
    builder.Services.AddScoped<ICachedMapperService, CachedMapperService>();

    // Transformation Services
    builder.Services.AddScoped<IMapperTransformationService, MapperTransformationService>();

    // Learning Services
    builder.Services.AddScoped<ExampleLearningService>();
    builder.Services.AddScoped<LayoutLearningService>();
    builder.Services.AddScoped<XmlFormatterService>();
    builder.Services.AddScoped<FileStorageService>();

    // Testing Services
    builder.Services.AddScoped<AutomatedTransformationTestService>();

    // Audit and Logging Services
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();
    builder.Services.AddScoped<ITechLogger, TechLogger>();
    builder.Services.AddScoped<AuditActionFilter>();

    var app = builder.Build();

    Log.Information("Application built successfully. Environment: {Environment}", app.Environment.EnvironmentName);

    // Configure the HTTP request pipeline.
    // CORS must be early in the pipeline - before routing and other middleware
    // This ensures CORS headers are sent even when errors occur
    app.UseCors();

    // Enable detailed error pages in development
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
        app.UseSwagger();
        app.UseSwaggerUI();
        Log.Information("Swagger UI enabled for development");
    }
    else
    {
        // Configure exception handler with a custom error handler
        // This ensures CORS headers are sent even when errors occur
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";

                var response = new
                {
                    error = "An error occurred while processing your request.",
                    message = app.Environment.IsDevelopment() ? exception?.Message : null
                };

                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(response));
            });
        });
    }

    // Only use HTTPS redirection if actually using HTTPS
    // app.UseHttpsRedirection();

    // Remove Authorization if not needed - it can interfere with CORS preflight
    // app.UseAuthorization();

    app.MapControllers();

    // Inicializar cache permanente de layouts e mapeadores na inicialização
    try
    {
        Log.Information("Iniciando populacao do cache permanente...");

        using (var scope = app.Services.CreateScope())
        {
            var cachedLayoutService = scope.ServiceProvider.GetRequiredService<ICachedLayoutService>();
            var cachedMapperService = scope.ServiceProvider.GetRequiredService<ICachedMapperService>();

            Log.Information("Populando cache de layouts...");

            // Popular cache de layouts
            try
            {
                await cachedLayoutService.RefreshCacheFromDatabaseAsync();
                Log.Information("Cache de layouts populado com sucesso");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Erro ao popular cache de layouts: {Message}", ex.Message);
            }

            // Popular cache de mapeadores
            Log.Information("Populando cache de mapeadores...");
            try
            {
                await cachedMapperService.RefreshCacheFromDatabaseAsync();
                Log.Information("Cache de mapeadores populado com sucesso");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Erro ao popular cache de mapeadores: {Message}", ex.Message);
                Log.Error(ex, "Stack trace: {StackTrace}", ex.StackTrace);
            }

            // Verificar se o cache foi criado
            try
            {
                var allMappers = await cachedMapperService.GetAllMappersAsync();
                Log.Information("Verificacao: {Count} mapeadores disponiveis no cache", allMappers?.Count ?? 0);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Erro ao verificar cache de mapeadores: {Message}", ex.Message);
            }

            Log.Information("Cache permanente inicializado com sucesso");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Erro ao inicializar cache permanente. A aplicacao continuara, mas o cache pode estar vazio.");
        Log.Error(ex, "Stack trace: {StackTrace}", ex.StackTrace);
    }

    var kestrelUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://0.0.0.0:5000";
    Log.Information("LayoutParserApi started successfully. Listening on: {Url}", kestrelUrl);
    Log.Information("CORS enabled for frontend origins");
    Log.Information("Log files are being written to: {LogDirectory}", logDirectory);

    Console.WriteLine("=".PadRight(80, '='));
    Console.WriteLine($"LayoutParserApi is starting...");
    Console.WriteLine($"Listening on: {kestrelUrl}");
    Console.WriteLine($"Log directory: {logDirectory}");
    Console.WriteLine("=".PadRight(80, '='));

    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"[FATAL ERROR] Application start-up failed: {ex.Message}");
    Console.WriteLine($"[FATAL ERROR] Stack trace: {ex.StackTrace}");

    try
    {
        Log.Fatal(ex, "Application start-up failed");
    }
    catch
    {
        // If logging fails, at least we have console output
    }

    throw;
}
finally
{
    try
    {
        Log.CloseAndFlush();
    }
    catch
    {
        // Ignore errors during shutdown
    }
}
