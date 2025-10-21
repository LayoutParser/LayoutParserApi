using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Logging
{
    public enum LoggerType
    {
        TextFile,
        ElasticSearch
    }

    public class LoggerFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public LoggerFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public ILoggerService CreateLogger(LoggerType loggerType)
        {
            return loggerType switch
            {
                LoggerType.TextFile => new TextFileLoggerService("Logs"),
                LoggerType.ElasticSearch => new ElasticSearchLoggerService(
                    "http://localhost:9200", // Configurar via appsettings
                    "layoutparser"),
                _ => new TextFileLoggerService("Logs")
            };
        }
    }
}