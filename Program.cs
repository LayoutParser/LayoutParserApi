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
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://172.25.32.42:81", "http://localhost:81", "http://localhost:8080")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
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
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
