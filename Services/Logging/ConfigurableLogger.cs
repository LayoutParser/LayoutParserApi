namespace LayoutParserApi.Services.Logging
{
    public class ConfigurableLogger
    {
        private readonly ILoggingStrategy _strategy;

        public ConfigurableLogger(ILoggingStrategy strategy)
        {
            _strategy = strategy;
        }

        public void LogInformation(string message, params object[] args)
        {
            _strategy.LogInformation(message, args);
        }

        public void LogWarning(string message, params object[] args)
        {
            _strategy.LogWarning(message, args);
        }

        public void LogError(Exception exception, string message, params object[] args)
        {
            _strategy.LogError(exception, message, args);
        }

        public void LogError(string message, params object[] args)
        {
            _strategy.LogError(message, args);
        }

        public void LogDebug(string message, params object[] args)
        {
            _strategy.LogDebug(message, args);
        }
    }
}
