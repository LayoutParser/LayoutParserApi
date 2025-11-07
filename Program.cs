using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Testing;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Implementations;
using StackExchange.Redis;
using Serilog;
using Serilog.Events;

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

    // Ensure log directory exists
    try
    {
        if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
            Console.WriteLine($"[BOOTSTRAP] Created log directory: {logDirectory}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[BOOTSTRAP] ERROR: Failed to create log directory '{logDirectory}': {ex.Message}");
        // Fallback to current directory
        logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }
        Console.WriteLine($"[BOOTSTRAP] Using fallback log directory: {logDirectory}");
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
    builder.Services.AddControllers();
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

    // Redis Configuration
    var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        try
        {
            Log.Information("Connecting to Redis at {RedisConnection}", redisConnectionString);
            return ConnectionMultiplexer.Connect(redisConnectionString);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to Redis at {RedisConnection}. The application will continue but Redis features will not work.", redisConnectionString);
            // Return a null or throw - depending on your requirements
            throw;
        }
    });

    // Cache Services
    builder.Services.AddScoped<ILayoutCacheService, LayoutCacheService>();

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
    builder.Services.AddScoped<LayoutParserApi.Services.Filters.AuditActionFilter>();

    var app = builder.Build();

    Log.Information("Application built successfully. Environment: {Environment}", app.Environment.EnvironmentName);

    // Configure the HTTP request pipeline.
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
        app.UseExceptionHandler("/Error");
    }

    // CORS must be early in the pipeline - before routing and other middleware
    app.UseCors();

    // Only use HTTPS redirection if actually using HTTPS
    // app.UseHttpsRedirection();

    // Remove Authorization if not needed - it can interfere with CORS preflight
    // app.UseAuthorization();

    app.MapControllers();

    var kestrelUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://0.0.0.0:5000";
    Log.Information("LayoutParserApi started successfully. Listening on: {Url}", kestrelUrl);
    Log.Information("CORS enabled for frontend origins");

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
