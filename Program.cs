using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Testing;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Transformation.Interface;
using LayoutParserApi.Services.Validation;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;

using Serilog;
using Serilog.Events;
using Serilog.Context;
using LayoutParserApi.Services.Logging;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

// ✅ Serviço Windows nativo: sob o SCM o processo inicia com CWD = System32, e o código
// resolve caminhos relativos via Directory.GetCurrentDirectory() em vários pontos
// (ex.: DocumentController, diretório de logs). Fixar o CWD na pasta do executável como
// PRIMEIRA instrução garante que o serviço se comporte igual à execução manual.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Bootstrap logger for errors before Serilog is configured
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ✅ Permite rodar como serviço Windows nativo (substitui o NSSM); integra o host ao
    // ciclo de vida do SCM. Fora do SCM (dotnet run / console) é no-op — zero impacto.
    builder.Host.UseWindowsService();

    // Configure Serilog for file and console logging
    var logDirectory = builder.Configuration["Logging:File:Directory"] ?? "Logs";
    // Nome base do arquivo (conforme requisito)
    var logFileName = builder.Configuration["Logging:File:FileName"] ?? "layoutparserapi.log";
    var retainedFileCountLimit = int.TryParse(builder.Configuration["Logging:File:RetainedFileCountLimit"], out var r) ? r : 10;
    var fileSizeLimitKb = int.TryParse(builder.Configuration["Logging:File:FileSizeLimitKB"], out var kb) ? kb : 2049;
    var fileSizeLimitBytes = (long)fileSizeLimitKb * 1024L;

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
                Directory.CreateDirectory(logDirectory);
            
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
                Directory.CreateDirectory(logDirectory);
            
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
            // ✅ Origem padrão de toda entrada é "Backend"; o endpoint de ingestão de log do
            // cliente (LogsController.PostClientLog) sobrescreve para "Frontend" só no escopo
            // da própria requisição via LogContext.PushProperty (mesmo padrão do CorrelationId).
            .Enrich.WithProperty("Source", "Backend")
            .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Corr:{CorrelationId}] [Src:{Source}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDirectory, logFileName),
                rollingInterval: RollingInterval.Infinite,
                retainedFileCountLimit: retainedFileCountLimit,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Corr:{CorrelationId}] [Src:{Source}] {Message:lj}{NewLine}{Exception}",
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
    builder.Services.AddControllers(options =>
        {
            // ✅ Porta de entrada por chave compartilhada para TODA a API. Registrado como filtro
            // GLOBAL, e não por controller, porque o problema não é um endpoint novo: são os 18
            // controllers que já existem servindo NF-e real sem credencial nenhuma.
            //
            // NASCE DESLIGADO por decisão de arquitetura: sem `Security:ApiKey` provisionada, o
            // filtro deixa passar (fail-OPEN). Ligá-lo fail-closed neste commit derrubaria o front
            // e o smoke test do próprio CI no deploy seguinte. Ver o comentário extenso em
            // ApiKeyGateFilter — inclusive a ordem de passos para ligar sem quebrar produção.
            options.Filters.Add<ApiKeyGateFilter>();
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            options.JsonSerializerOptions.WriteIndented = false;
            // ✅ Defesa em profundidade (QA/Quinn, Gap 3 — métricas de IA): normalmente NaN/Infinity
            // já são saneados na origem (AiMetricsReaderService.ParseDouble), mas caso algum outro
            // endpoint futuro exponha um double não-saneado, isso evita o 500 cru de
            // System.Text.Json ao serializar (em vez disso escreve o literal "NaN"/"Infinity").
            options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // ✅ Compressão de resposta. Este payload é o caso de uso ideal: a API devolve XML e JSON —
    // documento fiscal transformado, catálogo de layouts, páginas de log — e texto marcado comprime
    // na casa de 80-90%. O XML da transformação sozinho vai inline até 256 KB
    // (LowCode:InlineXmlMaxChars) e o front está do outro lado de uma rede corporativa.
    //
    // EnableForHttps fica no default (false) DE PROPÓSITO: comprimir sobre TLS reabre a classe de
    // ataque BREACH. Hoje a API é HTTP puro, então a compressão vale; quando o TLS entrar (§2.2 do
    // plano de arquitetura), reavaliar aqui em vez de ligar por reflexo.
    builder.Services.AddResponseCompression(options =>
    {
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        // O default cobre application/json; XML precisa ser declarado, e é justamente o payload
        // grande deste projeto.
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
        {
            "application/xml",
            "text/xml",
            "application/problem+json"
        });
    });
    // ⚠️ Os níveis NÃO são simétricos, e isso foi medido — não é preferência.
    // Benchmark em GET /api/monitoring/layouts-analysis (238.591 bytes crus, máquina de dev):
    //
    //   sem compressão      238.591 bytes   3602 ms
    //   brotli Fastest       77.865 bytes   3409 ms   <- pior que gzip nos DOIS eixos
    //   gzip   Fastest       69.119 bytes   2314 ms
    //   brotli Optimal       15.996 bytes   2200 ms   <- 93,3% de redução E o mais rápido
    //
    // Brotli em Fastest usa quality 1 e perde para gzip; só a partir de quality ~4 ele mostra o
    // que sabe fazer. Em Optimal comprime 4,3x melhor que o gzip e ainda termina ANTES da resposta
    // não comprimida — mandar 16 KB em vez de 238 KB paga a CPU com folga, mesmo no i7-4790 sem
    // GPU do servidor de produção.
    //
    // O gzip fica em Fastest de propósito: é só o fallback para cliente que não anuncia brotli
    // (Accept-Encoding), e nesse caminho o que importa é não custar caro.
    //
    // Caso pequeno medido: resposta de 32 bytes vira 37 com brotli — comprimir payload minúsculo
    // aumenta o tamanho. O middleware do ASP.NET não tem limiar mínimo, e não vale construir um:
    // o overhead é de bytes isolados contra uma economia de ~222 KB no caso que importa.
    builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
    builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

    // ✅ Teto explícito para upload de documento. O Kestrel já limita o corpo da request em ~30 MB
    // por default, mas o limite de multipart do ASP.NET é bem maior — e é multipart que o
    // ParseController recebe. Sem um teto declarado, o tamanho aceito passa a depender de qual
    // camada estoura primeiro, o que não é um limite, é um acidente.
    // 100 MB: os documentos posicionais reais medidos ficam na casa de dezenas de KB (a amostra do
    // gabarito tem 35 KB), então isto é folga de três ordens de grandeza — serve como barreira
    // contra corpo absurdo, não como restrição de uso.
    builder.Services.Configure<FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = 104_857_600;
    });

    // CORS Configuration
    // ✅ Origens configuráveis: env var CORS_ALLOWED_ORIGINS tem prioridade sobre
    // Cors:AllowedOrigins (appsettings). Fallback cobre os cenários de dev conhecidos
    // (front antigo em 80/81/8080 + LayoutParserReact build estático no IIS em 8081/3000).
    var corsAllowedOrigins = (Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS")
        ?? builder.Configuration["Cors:AllowedOrigins"]
        ?? "http://172.25.32.42:81,http://localhost:81,http://localhost:8080,http://172.25.32.42:80,http://localhost:80,http://127.0.0.1:81,http://127.0.0.1:8080,http://localhost:8081,http://localhost:3000")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    Log.Information("Configuring CORS with allowed origins: {AllowedOrigins}", string.Join(", ", corsAllowedOrigins));

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            // ✅ Métodos restritos ao que a API de fato expõe. Levantamento nos 18 controllers:
            // 36 endpoints [HttpPost] e 28 [HttpGet] — zero PUT, DELETE ou PATCH. AllowAnyMethod
            // autorizava um conjunto que não existe; declarar os três reais (OPTIONS é o preflight)
            // não muda comportamento nenhum do front e fecha a porta antes de alguém adicionar um
            // DELETE sem pensar no lado do CORS.
            //
            // AllowAnyHeader FICA: apertar header de request é o que mais quebra front, o ganho de
            // segurança é pequeno (header não é credencial por si) e a lista real varia com o que o
            // navegador manda. Não é descuido — é onde a relação custo/benefício inverte.
            //
            // WithExposedHeaders passou de "*" para a lista real. O wildcard entrega ao JS de
            // origem cruzada todo header de resposta, inclusive os que a app venha a adicionar
            // depois sem revisar CORS. X-Correlation-ID é o único header próprio que a API emite
            // (middleware logo abaixo) e o único que o front tem motivo para ler.
            policy.WithOrigins(corsAllowedOrigins)
                  .WithMethods("GET", "POST", "OPTIONS")
                  .AllowAnyHeader()
                  .WithExposedHeaders("X-Correlation-ID");
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
        Log.Warning("Redis is not available. Cache services will operate without Redis.");
    

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
    // ✅ Warm-up do cache permanente (layouts/mapeadores) roda em background APÓS app.Run(),
    // não bloqueia mais o startup / report ao Service Control Manager do Windows.
    builder.Services.AddHostedService<CachePermanentWarmupBackgroundService>();

    // XML Analysis Services
    builder.Services.AddScoped<XmlAnalysisService>();
    builder.Services.AddScoped<XsdValidationService>();
    builder.Services.AddScoped<XmlDocumentTypeDetector>();
    builder.Services.AddScoped<MqSeriesToXmlTransformer>();
    builder.Services.AddScoped<TransformationPipelineService>();
    builder.Services.AddScoped<TclGeneratorService>();
    builder.Services.AddScoped<XslGeneratorService>();
    builder.Services.AddScoped<AutoTransformationGeneratorService>();

    // ✅ Diagnóstico de erro de validação via Ollama (LLM local) — Gap 2 do contrato
    // docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md. Não usa GeminiAIService
    // (decomissionado, sem registro no DI — ver generation-services-unregistered-di.md).
    builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
    // ✅ Desliga o HttpClient.Timeout padrão (100s) do client tipado: o timeout real de
    // diagnóstico é o nosso próprio CancellationTokenSource (Ollama:DiagnosisTimeoutSeconds,
    // dentro de OllamaValidationDiagnosticService). Descoberto em teste manual: sem isso, o
    // timeout de 100s do HttpClient dispara ANTES do nosso e lança TaskCanceledException/
    // TimeoutException que o catch dedicado de timeout não reconhecia — caía no 500 genérico
    // em vez do 504 esperado pelo contrato (Gap 2).
    builder.Services.AddHttpClient<OllamaValidationDiagnosticService>(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
    });

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
    // ✅ Detector de anomalia por z-score sobre MLData/DocumentPatterns (P2 do roadmap)
    builder.Services.AddScoped<IDocumentAnomalyDetector, DocumentAnomalyDetectorService>();
    // ✅ Item 4.1/4.2 do roadmap de IA: cenários fiscais sintéticos rotulados (Bogus,
    // pt_BR, CPF/CNPJ válidos) para o índice RAG da IA fiscal - NÃO fixture de teste,
    // NÃO treino (sem GPU em produção). Depende de CfopOperationCatalogService (6.1).
    builder.Services.AddScoped<SyntheticFiscalScenarioGenerator>();
    // ✅ Item 4.4 do roadmap de IA: checagem de near-duplicate obrigatória para QUALQUER
    // saída "sintética" antes de indexar - este projeto é material de TCC.
    builder.Services.AddScoped<NearDuplicateChecker>();

    // RAG Services (retrieval de exemplos para geração sintética)
    // ✅ Fix incidental (item 1.4 do roadmap de IA, 2026-07-21): RAGService nunca esteve
    // registrado aqui, o que derruba o RAGController em runtime (falha de resolução de DI
    // ao primeiro request). RAGService é retrieval puro sobre arquivos locais - sem
    // dependência de nuvem (Gemini/OpenAI) - não relacionado à decisão de decommission.
    builder.Services.AddScoped<RAGService>();

    // Validation Services
    builder.Services.AddScoped<LayoutValidationService>();
    builder.Services.AddScoped<DocumentValidationService>();
    builder.Services.AddScoped<DocumentMLValidationService>();
    // ✅ Item 3.1 do roadmap de IA: validação determinística de campo de input
    // (tamanho/formato/checksum contra o LengthField do Layout XML) - sem IA.
    builder.Services.AddScoped<FieldContentValidationService>();
    // ✅ Item 3.2 do roadmap de IA: classificador determinístico de erro XSD
    // "esperado" (ex.: assinatura digital ausente) vs. defeito real - sem IA.
    builder.Services.AddScoped<XsdErrorClassifierService>();
    // ✅ Item 6.1 do roadmap de IA: base indexada CFOP x tipo de operação (lookup
    // determinístico puro, tabela pública CONFAZ/SEFAZ/Receita Federal) - sem IA.
    // Ainda sem controller consumidor (isso é o item 6.2, Lia+Dex) - registrado desde
    // já seguindo o mesmo padrão dos itens 3.1/3.2 acima.
    builder.Services.AddScoped<CfopOperationCatalogService>();
    builder.Services.Configure<LowCodeRunnerOptions>(builder.Configuration.GetSection("LowCode"));
    builder.Services.AddSingleton<LowCodeTransformationService>();
    // ✅ Store/índice das transformações low-code: Singleton porque é consumido pelos Singletons do
    // pathway (auto-transform) e não guarda estado por request. Redis é OPCIONAL — resolvido por
    // GetService (nullable), mesmo padrão dos caches de catálogo: sem Redis, tudo funciona por disco.
    builder.Services.AddSingleton<LowCodeTransformationStore>(sp => new LowCodeTransformationStore(
        sp.GetRequiredService<ILogger<LowCodeTransformationStore>>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<IOptions<LowCodeRunnerOptions>>(),
        sp.GetService<IConnectionMultiplexer>()));
    builder.Services.AddSingleton<LowCodeAutoTransformationService>();
    builder.Services.AddHostedService<LayoutValidationBackgroundService>();

    // Testing Services
    builder.Services.AddScoped<AutomatedTransformationTestService>();

    // Audit and Logging Services
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();
    builder.Services.AddScoped<ITechLogger, TechLogger>();
    builder.Services.AddScoped<AuditActionFilter>();
    // ✅ Leitura unificada dos 3 arquivos de log do ecossistema (Api/Lib/Decrypt) para
    // GET api/logs (LogsController) — desenho de logging unificado fechado pela arquiteta.
    builder.Services.AddScoped<IUnifiedLogReaderService, UnifiedLogReaderService>();

    // ✅ Gap 3 (painel de métricas de IA): parseia por cima do mesmo log unificado acima
    // (Source=AiMetrics), sem leitura/rotação de arquivo própria.
    builder.Services.AddScoped<IAiMetricsReaderService, AiMetricsReaderService>();

    // ✅ Contraparte de escrita do leitor acima: recebe as gerações do job que roda na VM Linux
    // (POST api/ai-metrics/generations/ingest) e as grava no MESMO log que o leitor consome —
    // sem isso o painel lê um diretório que nunca recebe geração nenhuma.
    builder.Services.AddScoped<IAiMetricsIngestService, AiMetricsIngestService>();

    // ✅ Chave compartilhada dos DOIS endpoints de escrita de métricas de IA (mesmo padrão de
    // registro do AuditActionFilter acima, aplicado via [ServiceFilter] no controller). Fail-closed:
    // sem AiMetrics__IngestApiKey no ambiente, a escrita é recusada — ver AiMetricsIngestKeyFilter.
    builder.Services.AddScoped<AiMetricsIngestKeyFilter>();

    // ✅ Porta de entrada global da API (registrada como filtro em AddControllers, acima).
    // Scoped pelo mesmo motivo do filtro acima: depende de IConfiguration e ILogger, sem estado.
    builder.Services.AddScoped<ApiKeyGateFilter>();

    var app = builder.Build();

    Log.Information("Application built successfully. Environment: {Environment}", app.Environment.EnvironmentName);

    // ✅ Postura de acesso da API, dita UMA vez no arranque. O filtro global não loga por request
    // (seria uma linha por chamada num log que é relido pelo painel), então este é o único lugar
    // onde "a API está aberta" fica registrado — e é uma informação que ninguém deveria descobrir
    // por acidente.
    if (string.IsNullOrWhiteSpace(builder.Configuration[ApiKeyGateFilter.ConfigKey]))
    {
        Log.Warning("API SEM AUTENTICACAO: {ConfigKey} nao configurada, todos os endpoints aceitam requisicao anonima. " +
                    "Para fechar, provisione a variavel de ambiente Security__ApiKey (e liste em Security__AnonymousPaths o que " +
                    "precisa seguir anonimo). Ver ApiKeyGateFilter antes de ligar em producao.",
                    ApiKeyGateFilter.ConfigKey);
    }
    else
    {
        var rotasAnonimas = builder.Configuration.GetSection(ApiKeyGateFilter.AnonymousPathsKey).Get<string[]>() ?? Array.Empty<string>();
        Log.Information("Porta de entrada por chave ATIVA (header {Header}). Rotas anonimas: {Rotas}",
            ApiKeyGateFilter.HeaderName,
            rotasAnonimas.Length > 0 ? string.Join(", ", rotasAnonimas) : "(nenhuma)");
    }

    // ✅ Diagnóstico de arranque da config low-code. Mesmo princípio do Redis acima: AVISA e segue,
    // nunca derruba a app — parse e catálogo funcionam sem transformação.
    //
    // POR QUE ISTO EXISTE: o deploy PRESERVA o appsettings.json do destino (ci-dev.yml/deploy.yml
    // copiam com `-Exclude appsettings.json`), então chave nova adicionada ao appsettings.json do
    // REPO nunca chega ao servidor — o valor efetivo vem de variável de ambiente `Section__Key`.
    // Quando esse canal falha, a falha aparecia longe da causa: o runner saía com exit 9 e o usuário
    // via "transformação falhou", uma requisição por vez, depois de pagar o custo de subir um
    // processo x86. Ler os valores EFETIVOS aqui (pós-binding, já com env vars aplicadas) transforma
    // um mistério de runtime numa linha de log no arranque.
    try
    {
        var lowCodeOpt = app.Services.GetRequiredService<IOptions<LowCodeRunnerOptions>>().Value;

        if (string.IsNullOrWhiteSpace(lowCodeOpt.Package))
            Log.Warning("LowCode:Package VAZIO — toda transformação low-code vai falhar (runner sai com exit 9). " +
                        "Sem o appConnector o package não tem mais fallback. Configure a variável de ambiente LowCode__Package " +
                        "com o <PackageMappers> do config.xml da instância Sysmiddle.");
        else
            Log.Information("LowCode:Package configurado: {Package}", lowCodeOpt.Package);

        if (string.IsNullOrWhiteSpace(lowCodeOpt.RunnerPath))
            Log.Warning("LowCode:RunnerPath não configurado — nenhuma transformação low-code é possível.");
        else if (!File.Exists(lowCodeOpt.RunnerPath))
            Log.Warning("LowCode:RunnerPath aponta para um arquivo que NÃO EXISTE: {RunnerPath}. " +
                        "Toda transformação low-code vai falhar até o runner ser publicado neste host.", lowCodeOpt.RunnerPath);

        foreach (var (chave, valor) in new[]
                 {
                     ("LowCode:SysmiddleDir", lowCodeOpt.SysmiddleDir),
                     ("LowCode:GlobalFolder", lowCodeOpt.GlobalFolder)
                 })
        {
            if (string.IsNullOrWhiteSpace(valor))
                Log.Warning("{Chave} não configurado — a transformação low-code falha na validação de entrada.", chave);
        }

        // 60s é o piso empírico, não um número redondo: a medição A/B por fase mostrou a
        // transformação completa entre 48s e 137s (só o ExecuteMapper leva 38-73s). Abaixo disso o
        // processo é morto no meio de trabalho bom — a falha mais cara que este pathway tem, porque
        // consome o slot inteiro e não deixa resultado.
        if (lowCodeOpt.RunnerTimeoutSeconds > 0 && lowCodeOpt.RunnerTimeoutSeconds < 60)
            Log.Warning("LowCode:RunnerTimeoutSeconds = {Timeout}s é INVIÁVEL — a transformação real leva 48-137s medidos. " +
                        "O runner será morto no meio da execução. Configure LowCode__RunnerTimeoutSeconds (referência: 180).",
                        lowCodeOpt.RunnerTimeoutSeconds);
    }
    catch (Exception ex)
    {
        // Diagnóstico nunca pode ser o motivo de a app não subir.
        Log.Warning(ex, "Falha ao inspecionar a configuração low-code no arranque (diagnóstico ignorado).");
    }

    // ✅ Compressão primeiro no pipeline: precisa envolver tudo que escreve no corpo da resposta,
    // inclusive o handler de exceção mais abaixo. Depois que outro middleware começou a escrever,
    // é tarde para comprimir.
    app.UseResponseCompression();

    // ✅ Cabeçalhos de segurança na resposta. Baratos, sem efeito sobre o contrato da API, e
    // fecham três vetores que hoje estão abertos:
    //   nosniff        — impede o navegador de "adivinhar" que um XML de documento é HTML e
    //                    executá-lo; relevante aqui porque a API devolve markup de terceiro.
    //   DENY (frame)   — a API não tem UI própria; nada dela deve ser embutido em iframe.
    //   no-referrer    — evita vazar a URL (que carrega nome de layout e ticket) para terceiros.
    //
    // HSTS deliberadamente AUSENTE: só faz sentido sobre TLS, e a app serve HTTP puro hoje
    // (UseHttpsRedirection segue comentado abaixo). Enviá-lo agora não protegeria nada e poderia
    // travar o acesso ao host no navegador de quem testa. Entra junto com o TLS, não antes.
    app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        await next();
    });

    // Configure the HTTP request pipeline.
    // CORS must be early in the pipeline - before routing and other middleware
    // This ensures CORS headers are sent even when errors occur
    app.UseCors();

    // ✅ CorrelationId (GUID) por request/arquivo processado
    // - Aceita header X-Correlation-ID vindo do front (se existir)
    // - Caso não exista, gera um novo GUID
    // - Devolve no response header X-Correlation-ID
    // - Injeta no Serilog LogContext, aparecendo em todos os logs como Corr:{CorrelationId}
    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString();

        context.Response.Headers["X-Correlation-ID"] = correlationId;
        CorrelationContext.CurrentId = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next();
        }
    });

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

    // Nota: a população do cache permanente de layouts/mapeadores foi movida para
    // CachePermanentWarmupBackgroundService (Services/Database), que roda como IHostedService
    // em segundo plano após app.Run(). Isso evita bloquear o startup (e o report ao Service
    // Control Manager do Windows) enquanto aguarda o SQL Server responder.

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
