using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace LayoutParserApi.Tests.Filters
{
    /// <summary>
    /// Garante que o <see cref="AuditActionFilter"/> registra QUEM fez a ação — a identidade que o
    /// <c>TrustedIdentityMiddleware</c> preencheu em <see cref="ICurrentUser"/> chega ao log de
    /// auditoria. Sem identidade, o registro cai para "anon", nunca lança.
    /// </summary>
    public class AuditActionFilterTests
    {
        [Fact]
        public void Usuario_autenticado_e_gravado_no_audit()
        {
            var logger = new AuditLoggerFake();
            var filtro = new AuditActionFilter(logger, new CurrentUserStub("alice", "admin"));

            filtro.OnActionExecuting(CriarExecutingContext());

            var entrada = Assert.Single(logger.Entradas);
            Assert.Equal("alice", entrada.UserId);
        }

        [Fact]
        public void Usuario_anonimo_vira_anon()
        {
            var logger = new AuditLoggerFake();
            var filtro = new AuditActionFilter(logger, CurrentUserStub.Anonimo);

            filtro.OnActionExecuting(CriarExecutingContext());

            var entrada = Assert.Single(logger.Entradas);
            Assert.Equal("anon", entrada.UserId);
        }

        [Fact]
        public void Order_e_anterior_ao_filtro_de_ModelState_do_ApiController()
        {
            // Issue #31: o [ApiController] registra um filtro interno (Order = -3000) que
            // curto-circuita a pipeline quando o ModelState é inválido, antes de qualquer
            // ActionFilter com Order padrão (0) rodar. Este teste trava a garantia estrutural
            // de que o AuditActionFilter roda ANTES desse filtro (Order menor), condição
            // necessária para que uma requisição malformada também gere linha AUDIT.
            var filtro = new AuditActionFilter(new AuditLoggerFake(), CurrentUserStub.Anonimo);

            Assert.True(filtro.Order < -3000);
        }

        [Fact]
        public void Executed_tambem_carrega_o_usuario()
        {
            var logger = new AuditLoggerFake();
            var filtro = new AuditActionFilter(logger, new CurrentUserStub("bob", "operador"));

            filtro.OnActionExecuted(CriarExecutedContext());

            var entrada = Assert.Single(logger.Entradas);
            Assert.Equal("bob", entrada.UserId);
        }

        // --- helpers ---

        private static ActionExecutingContext CriarExecutingContext()
        {
            return new ActionExecutingContext(
                CriarActionContext(),
                new List<IFilterMetadata>(),
                new Dictionary<string, object?>(),
                controller: new object());
        }

        private static ActionExecutedContext CriarExecutedContext()
        {
            return new ActionExecutedContext(
                CriarActionContext(),
                new List<IFilterMetadata>(),
                controller: new object());
        }

        private static ActionContext CriarActionContext()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/api/parse/upload";
            return new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        }

        private sealed class AuditLoggerFake : IAuditLogger
        {
            public List<AuditLogEntry> Entradas { get; } = new();

            public void LogAudit(AuditLogEntry entry) => Entradas.Add(entry);
        }

        private sealed class CurrentUserStub : ICurrentUser
        {
            public static readonly CurrentUserStub Anonimo = new(null);

            public CurrentUserStub(string? name, params string[] roles)
            {
                Name = name;
                Roles = roles;
            }

            public string? Name { get; }
            public IReadOnlyList<string> Roles { get; }
            public bool IsAuthenticated => !string.IsNullOrEmpty(Name);
            public bool IsInRole(string role) => Roles.Contains(role);
        }
    }
}
