using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc.Filters;

namespace LayoutParserApi.Services.Filters
{
    public class AuditActionFilter : IActionFilter
    {
        private readonly IAuditLogger _auditLogger;

        public AuditActionFilter(IAuditLogger auditLogger)
        {
            _auditLogger = auditLogger;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            var httpContext = context.HttpContext;
            var userId = httpContext.User.Identity?.Name ?? "anon";
            var requestId = httpContext.TraceIdentifier;
            var endpoint = httpContext.Request.Path;

            _auditLogger.LogAudit(new AuditLogEntry
            {
                UserId = userId,
                RequestId = requestId,
                Endpoint = endpoint,
                Action = context.ActionDescriptor.DisplayName,
                Details = "Request iniciada"
            });
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
            var userId = context.HttpContext.User?.Identity?.Name ?? "anonymous";
            var requestId = context.HttpContext.TraceIdentifier;
            var endpoint = context.ActionDescriptor.DisplayName;
            var timestamp = DateTime.UtcNow;

            _auditLogger.LogAudit(new AuditLogEntry
            {
                UserId = userId,
                RequestId = requestId,
                Endpoint = endpoint,
                Timestamp = timestamp,
                Action = "Executed"
            });
        }
    }
}
