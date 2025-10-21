using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Logging
{
    public class AuditLoggerAdapter : IAuditLogger
    {
        private readonly ILoggerService _loggerService;
        private readonly IRequestContextService _requestContext;

        public AuditLoggerAdapter(ILoggerService loggerService, IRequestContextService requestContext)
        {
            _loggerService = loggerService;
            _requestContext = requestContext;
        }

        public void LogAudit(AuditLogEntry entry)
        {
            entry.RequestId = string.IsNullOrEmpty(entry.RequestId) ?
                _requestContext.GetCurrentRequestId() : entry.RequestId;

            _loggerService.LogAuditAsync(entry).GetAwaiter().GetResult();
        }
    }
}