using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc.Filters;

namespace LayoutParserApi.Services.Filters
{
    public class AuditActionFilter : IActionFilter
    {
        private readonly IAuditLogger _auditLogger;
        private readonly ICurrentUser _currentUser;

        // ICurrentUser é a fonte da identidade (preenchida pelo TrustedIdentityMiddleware sob a guarda
        // de loopback). O registro de auditoria passa a carregar QUEM fez a ação; anônimo vira "anon".
        public AuditActionFilter(IAuditLogger auditLogger, ICurrentUser currentUser)
        {
            _auditLogger = auditLogger;
            _currentUser = currentUser;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            var httpContext = context.HttpContext;
            var userId = _currentUser.IsAuthenticated ? _currentUser.Name! : "anon";
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
            var userId = _currentUser.IsAuthenticated ? _currentUser.Name! : "anon";
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
