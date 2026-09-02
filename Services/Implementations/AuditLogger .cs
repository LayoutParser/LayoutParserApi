using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Implementations
{
    public class AuditLogger : IAuditLogger
    {
        private readonly ILogger<AuditLogger> _logger;

        public AuditLogger(ILogger<AuditLogger> logger)
        {
            _logger = logger;
        }

        public void LogAudit(AuditLogEntry entry)
        {
            // ✅ CodeQL cs/log-forging: entry.* vem do request (endpoint/ação/detalhes do usuário) e
            // este log também é fonte de auditoria — CRLF cru forjaria linhas de auditoria falsas.
            _logger.LogInformation("AUDIT | UserId:{UserId} | RequestId:{RequestId} | Endpoint:{Endpoint} | Action:{Action} | Details:{Details}",
                Services.Logging.LogMessageSanitizer.Sanitize(entry.UserId),
                Services.Logging.LogMessageSanitizer.Sanitize(entry.RequestId),
                Services.Logging.LogMessageSanitizer.Sanitize(entry.Endpoint),
                Services.Logging.LogMessageSanitizer.Sanitize(entry.Action),
                Services.Logging.LogMessageSanitizer.Sanitize(entry.Details));
        }
    }

    public class TechLogger : ITechLogger
    {
        private readonly ILogger<TechLogger> _logger;

        public TechLogger(ILogger<TechLogger> logger)
        {
            _logger = logger;
        }

        public void LogTechnical(LogEntry entry)
        {
            // ✅ CodeQL cs/log-forging: mesmo motivo do AuditLogger acima — entry.* é derivado do request.
            if (entry.Level == "Error" && entry.Exception != null)
                _logger.LogError(entry.Exception, "TECH | RequestId:{RequestId} | Endpoint:{Endpoint} | Message:{Message}",
                    Services.Logging.LogMessageSanitizer.Sanitize(entry.RequestId),
                    Services.Logging.LogMessageSanitizer.Sanitize(entry.Endpoint),
                    Services.Logging.LogMessageSanitizer.Sanitize(entry.Message));
            else
                _logger.LogInformation("TECH | Level:{Level} | RequestId:{RequestId} | Endpoint:{Endpoint} | Message:{Message}",
                    Services.Logging.LogMessageSanitizer.Sanitize(entry.Level),
                    Services.Logging.LogMessageSanitizer.Sanitize(entry.RequestId),
                    Services.Logging.LogMessageSanitizer.Sanitize(entry.Endpoint),
                    Services.Logging.LogMessageSanitizer.Sanitize(entry.Message));
        }
    }

}
