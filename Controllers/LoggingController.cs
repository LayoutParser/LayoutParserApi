using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LoggingController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<LoggingController> _logger;

        public LoggingController(IConfiguration configuration, ILogger<LoggingController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("config")]
        public IActionResult GetLoggingConfig()
        {
            try
            {
                var loggingType = _configuration["Logging:Type"] ?? "file";
                var logDirectory = _configuration["Logging:File:Directory"] ?? "Logs";
                var logFileName = _configuration["Logging:File:FileName"] ?? "layoutparserapi-{0:yyyy-MM-dd}.log";

                var config = new
                {
                    currentType = loggingType,
                    fileConfig = new
                    {
                        directory = logDirectory,
                        fileName = logFileName,
                        fullPath = Path.Combine(logDirectory, string.Format(logFileName, DateTime.Now))
                    },
                    elasticSearchConfig = new
                    {
                        url = _configuration["ElasticSearch:Url"],
                        username = _configuration["ElasticSearch:Username"],
                        enabled = loggingType == "elasticsearch"
                    }
                };

                return Ok(new
                {
                    success = true,
                    config = config
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter configuração de logging");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("files")]
        public IActionResult GetLogFiles()
        {
            try
            {
                var logDirectory = _configuration["Logging:File:Directory"] ?? "Logs";
                
                if (!Directory.Exists(logDirectory))
                    return Ok(new
                    {
                        success = true,
                        message = "Diretório de logs não existe",
                        files = new List<object>()
                    });

                var logFiles = Directory.GetFiles(logDirectory, "*.log")
                    .Select(file => new
                    {
                        fileName = Path.GetFileName(file),
                        filePath = file,
                        lastModified = System.IO.File.GetLastWriteTime(file),
                        size = new FileInfo(file).Length,
                        sizeFormatted = FormatFileSize(new FileInfo(file).Length)
                    })
                    .OrderByDescending(f => f.lastModified)
                    .ToList();

                return Ok(new
                {
                    success = true,
                    directory = logDirectory,
                    count = logFiles.Count,
                    files = logFiles
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar arquivos de log");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("file/{fileName}")]
        public IActionResult GetLogFile(string fileName)
        {
            try
            {
                var logDirectory = _configuration["Logging:File:Directory"] ?? "Logs";
                var filePath = Path.Combine(logDirectory, fileName);

                if (!System.IO.File.Exists(filePath))
                    return NotFound($"Arquivo de log {fileName} não encontrado");

                var content = System.IO.File.ReadAllText(filePath);
                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                return Ok(new
                {
                    success = true,
                    fileName = fileName,
                    lineCount = lines.Length,
                    size = content.Length,
                    lastModified = System.IO.File.GetLastWriteTime(filePath),
                    content = content
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao ler arquivo de log {FileName}", fileName);
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpPost("test")]
        public IActionResult TestLogging()
        {
            try
            {
                _logger.LogInformation("TEST_LOG_INFO | Teste de logging de informação | Timestamp: {Timestamp}", DateTime.Now);
                _logger.LogWarning("TEST_LOG_WARN | Teste de logging de aviso | Timestamp: {Timestamp}", DateTime.Now);
                _logger.LogError("TEST_LOG_ERROR | Teste de logging de erro | Timestamp: {Timestamp}", DateTime.Now);

                return Ok(new
                {
                    success = true,
                    message = "Logs de teste enviados com sucesso",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante teste de logging");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpDelete("cleanup")]
        public IActionResult CleanupOldLogs(int daysToKeep = 7)
        {
            try
            {
                var logDirectory = _configuration["Logging:File:Directory"] ?? "Logs";
                
                if (!Directory.Exists(logDirectory))
                    return Ok(new
                    {
                        success = true,
                        message = "Diretório de logs não existe",
                        deletedFiles = 0
                    });

                var cutoffDate = DateTime.Now.AddDays(-daysToKeep);
                var filesToDelete = Directory.GetFiles(logDirectory, "*.log")
                    .Where(file => System.IO.File.GetLastWriteTime(file) < cutoffDate)
                    .ToList();

                var deletedCount = 0;
                foreach (var file in filesToDelete)
                {
                    try
                    {
                        System.IO.File.Delete(file);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Não foi possível deletar arquivo {FileName}", file);
                    }
                }

                return Ok(new
                {
                    success = true,
                    message = $"Limpeza concluída. {deletedCount} arquivos deletados.",
                    deletedFiles = deletedCount,
                    cutoffDate = cutoffDate
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante limpeza de logs");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
