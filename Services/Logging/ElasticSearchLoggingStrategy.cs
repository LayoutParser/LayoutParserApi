using Serilog;

namespace LayoutParserApi.Services.Logging
{
    public class ElasticSearchLoggingStrategy : ILoggingStrategy
    {
        private readonly Serilog.ILogger _logger;

        public ElasticSearchLoggingStrategy(Serilog.ILogger logger)
        {
            _logger = logger;
        }

        public void LogInformation(string message, params object[] args)
        {
            _logger.Information(message, args);
        }

        public void LogWarning(string message, params object[] args)
        {
            _logger.Warning(message, args);
        }

        public void LogError(Exception exception, string message, params object[] args)
        {
            _logger.Error(exception, message, args);
        }

        public void LogError(string message, params object[] args)
        {
            _logger.Error(message, args);
        }

        public void LogDebug(string message, params object[] args)
        {
            _logger.Debug(message, args);
        }
    }
}
