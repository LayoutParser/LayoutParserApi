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
            _logger.LogInformation("AUDIT | UserId:{UserId} | RequestId:{RequestId} | Endpoint:{Endpoint} | Action:{Action} | Details:{Details}",entry.UserId, entry.RequestId, entry.Endpoint, entry.Action, entry.Details);
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
            if (entry.Level == "Error" && entry.Exception != null)
                _logger.LogError(entry.Exception,"TECH | RequestId:{RequestId} | Endpoint:{Endpoint} | Message:{Message}",entry.RequestId, entry.Endpoint, entry.Message);
            else
                _logger.LogInformation("TECH | Level:{Level} | RequestId:{RequestId} | Endpoint:{Endpoint} | Message:{Message}",entry.Level, entry.RequestId, entry.Endpoint, entry.Message);
        }
    }

}
