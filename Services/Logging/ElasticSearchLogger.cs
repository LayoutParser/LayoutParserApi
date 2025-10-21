using Serilog;
using Serilog.Sinks.Elasticsearch;
using Serilog.Formatting.Elasticsearch;
using System.Reflection;

namespace LayoutParserApi.Services.Logging
{
    public static class ElasticSearchLogger
    {
        public static Serilog.ILogger CreateLogger(IConfiguration configuration, string environment)
        {
            var elasticSearchUrl = configuration["ElasticSearch:Url"] ?? "https://localhost:9200";
            var elasticSearchUsername = configuration["ElasticSearch:Username"] ?? "elastic";
            var elasticSearchPassword = configuration["ElasticSearch:Password"] ?? "123";
            var applicationName = Assembly.GetExecutingAssembly().GetName().Name;

            return new LoggerConfiguration()
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", applicationName)
                .Enrich.WithProperty("Environment", environment)
                .Enrich.WithProperty("Version", Assembly.GetExecutingAssembly().GetName().Version?.ToString())
                .WriteTo.Console()
                .WriteTo.Async(a => a.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticSearchUrl))
                {
                    AutoRegisterTemplate = true,
                    IndexFormat = $"{applicationName.ToLower()}-tech-logs-{{0:yyyy.MM.dd}}",
                    ModifyConnectionSettings = x => x
                        .BasicAuthentication(elasticSearchUsername, elasticSearchPassword)
                        .ServerCertificateValidationCallback((a, b, c, d) => true),
                    CustomFormatter = new ExceptionAsObjectJsonFormatter(renderMessage: true),
                    EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog
                }))
                .WriteTo.Async(a => a.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticSearchUrl))
                {
                    AutoRegisterTemplate = true,
                    IndexFormat = $"{applicationName.ToLower()}-audit-logs-{{0:yyyy.MM.dd}}",
                    ModifyConnectionSettings = x => x
                        .BasicAuthentication(elasticSearchUsername, elasticSearchPassword)
                        .ServerCertificateValidationCallback((a, b, c, d) => true),
                    CustomFormatter = new ExceptionAsObjectJsonFormatter(renderMessage: true),
                    EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog
                }))
                .WriteTo.Async(a => a.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticSearchUrl))
                {
                    AutoRegisterTemplate = true,
                    IndexFormat = $"{applicationName.ToLower()}-data-generation-{{0:yyyy.MM.dd}}",
                    ModifyConnectionSettings = x => x
                        .BasicAuthentication(elasticSearchUsername, elasticSearchPassword)
                        .ServerCertificateValidationCallback((a, b, c, d) => true),
                    CustomFormatter = new ExceptionAsObjectJsonFormatter(renderMessage: true),
                    EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog
                }))
                .CreateLogger();
        }
    }
}
