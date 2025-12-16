using LayoutParserApi.Services.Interfaces;

using Serilog;

namespace LayoutParserApi.Services.Logging
{
    public static class LoggingStrategyFactory
    {
        public static ILoggingStrategy CreateStrategy(IConfiguration configuration, string environment)
        {
            var loggingType = configuration["Logging:Type"]?.ToLower() ?? "file";

            return loggingType switch
            {
                "elasticsearch" => CreateElasticSearchStrategy(configuration, environment),
                "file" => new FileLoggingStrategy(configuration),
                "console" => new ConsoleLoggingStrategy(),
                _ => new FileLoggingStrategy(configuration)
            };
        }

        private static ILoggingStrategy CreateElasticSearchStrategy(IConfiguration configuration, string environment)
        {
            var logger = ElasticSearchLogger.CreateLogger(configuration, environment);
            return new ElasticSearchLoggingStrategy(logger);
        }
    }

    public class ConsoleLoggingStrategy : ILoggingStrategy
    {
        public void LogInformation(string message, params object[] args)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {string.Format(message, args)}");
        }

        public void LogWarning(string message, params object[] args)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] {string.Format(message, args)}");
        }

        public void LogError(Exception exception, string message, params object[] args)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ERROR] {string.Format(message, args)} | Exception: {exception.Message}");
        }

        public void LogError(string message, params object[] args)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ERROR] {string.Format(message, args)}");
        }

        public void LogDebug(string message, params object[] args)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] {string.Format(message, args)}");
        }
    }
}
