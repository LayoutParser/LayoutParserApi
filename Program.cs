using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Testing;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Implementations;
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

// Audit and Logging Services
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<ITechLogger, TechLogger>();
builder.Services.AddScoped<LayoutParserApi.Services.Filters.AuditActionFilter>();

var app = builder.Build();

// Configure the HTTP request pipeline.
// Enable detailed error pages in development
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
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

app.Run();
