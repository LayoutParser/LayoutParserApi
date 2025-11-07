using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Testing;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

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
                "http://localhost:80"
              )
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Redis Configuration
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    return ConnectionMultiplexer.Connect(redisConnectionString);
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

var app = builder.Build();

// Configure the HTTP request pipeline.
// Handle CORS preflight and add CORS headers to all responses
app.Use(async (context, next) =>
{
    var allowedOrigins = new[] { "http://172.25.32.42:81", "http://localhost:81", "http://localhost:8080", "http://172.25.32.42:80", "http://localhost:80" };
    var origin = context.Request.Headers["Origin"].ToString();
    
    if (allowedOrigins.Contains(origin))
    {
        context.Response.Headers.Add("Access-Control-Allow-Origin", origin);
        context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS, PATCH");
        context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Requested-With, Accept");
        context.Response.Headers.Add("Access-Control-Max-Age", "3600");
    }
    
    if (context.Request.Method == "OPTIONS")
    {
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync("");
        return;
    }
    
    await next();
});

// CORS must be first in the pipeline - before any other middleware
app.UseCors();

// Only use HTTPS redirection if actually using HTTPS
// app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Remove Authorization if not needed - it can interfere with CORS preflight
// app.UseAuthorization();

app.MapControllers();

app.Run();
