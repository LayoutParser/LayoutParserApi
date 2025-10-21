using System.Text;

namespace LayoutParserApi.Services.Logging
{
    public class FileLoggingStrategy : ILoggingStrategy
    {
        private readonly string _logFilePath;
        private readonly object _lockObject = new object();

        public FileLoggingStrategy(IConfiguration configuration)
        {
            var logDirectory = configuration["Logging:File:Directory"] ?? "Logs";
            var logFileName = configuration["Logging:File:FileName"] ?? "layoutparserapi-{0:yyyy-MM-dd}.log";
            
            // Criar diretório se não existir
            if (!Directory.Exists(logDirectory))
                Directory.CreateDirectory(logDirectory);

            _logFilePath = Path.Combine(logDirectory, string.Format(logFileName, DateTime.Now));
        }

        public void LogInformation(string message, params object[] args)
        {
            WriteLog("INFO", message, args);
        }

        public void LogWarning(string message, params object[] args)
        {
            WriteLog("WARN", message, args);
        }

        public void LogError(Exception exception, string message, params object[] args)
        {
            var fullMessage = $"{string.Format(message, args)} | Exception: {exception.Message} | StackTrace: {exception.StackTrace}";
            WriteLog("ERROR", fullMessage);
        }

        public void LogError(string message, params object[] args)
        {
            WriteLog("ERROR", message, args);
        }

        public void LogDebug(string message, params object[] args)
        {
            WriteLog("DEBUG", message, args);
        }

        private void WriteLog(string level, string message, params object[] args)
        {
            try
            {
                var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {formattedMessage}{Environment.NewLine}";

                lock (_lockObject)
                {
                    File.AppendAllText(_logFilePath, logEntry, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // Fallback para console se houver erro no arquivo
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ERROR] Falha ao escrever log: {ex.Message}");
            }
        }
    }
}
