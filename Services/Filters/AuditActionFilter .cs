using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc.Filters;

namespace LayoutParserApi.Services.Filters
{
    public class AuditActionFilter : IActionFilter, IOrderedFilter
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

        // ⚠️ Issue #31: o [ApiController] registra automaticamente um filtro interno
        // (ModelStateInvalidFilterFactory) com Order = -3000 que curto-circuita a pipeline
        // (seta context.Result = BadRequest) ANTES de qualquer outro ActionFilter rodar quando
        // o ModelState é inválido. Com Order padrão (0), o AuditActionFilter nunca chegava a
        // executar OnActionExecuting nesse caso — request malformada não gerava linha AUDIT.
        // Fixamos Order abaixo de -3000 para que este filtro seja o PRIMEIRO da pipeline: ele
        // roda antes do filtro de validação do model state e, mesmo que este curto-circuite
        // depois, o pipeline de ActionFilter ainda chama OnActionExecuted dos filtros que já
        // haviam entrado (unwind), então a auditoria também é registrada nesse caminho.
        public int Order => int.MinValue;

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
