using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Logging
{
    public class TechLoggerAdapter : ITechLogger
    {
        private readonly ILoggerService _loggerService;
        private readonly IRequestContextService _requestContext;

        public TechLoggerAdapter(ILoggerService loggerService, IRequestContextService requestContext)
        {
            _loggerService = loggerService;
            _requestContext = requestContext;
        }

        public void LogTechnical(LogEntry entry)
        {
            // Mantém o RequestId existente ou usa o do contexto
            entry.RequestId = string.IsNullOrEmpty(entry.RequestId) ?
                _requestContext.GetCurrentRequestId() : entry.RequestId;

            _loggerService.LogTechnicalAsync(entry).GetAwaiter().GetResult();
        }
    }
}