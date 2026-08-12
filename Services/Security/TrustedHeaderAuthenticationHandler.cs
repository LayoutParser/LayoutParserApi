using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Handler de autenticação mínimo que NÃO autentica nada por conta própria: apenas lê o
    /// <see cref="HttpContext.User"/> já preenchido pelo <see cref="TrustedIdentityMiddleware"/>
    /// (que roda antes, na pipeline) e o expõe ao <c>AuthorizationMiddleware</c>.
    /// </summary>
    /// <remarks>
    /// Existe só porque <c>[Authorize(Roles = "...")]</c> (issue #32) precisa de um esquema de
    /// autenticação registrado — sem ele, o <c>AuthorizationMiddleware</c> lança ao tentar
    /// desafiar (401) ou negar (403) uma requisição, mesmo com <c>HttpContext.User</c> já
    /// populado por fora. A fonte da verdade da identidade continua sendo exclusivamente o
    /// <see cref="TrustedIdentityMiddleware"/> (guarda de loopback); este handler não lê headers,
    /// não decide identidade, só "formaliza" o que já está em <c>HttpContext.User</c> para o
    /// pipeline padrão do ASP.NET Core.
    /// </remarks>
    public class TrustedHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TrustedHeader";

        public TrustedHeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Context.User;

            if (user?.Identity?.IsAuthenticated == true)
            {
                var ticket = new AuthenticationTicket(user, Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            // Sem identidade confiável (fora de loopback, sem header, etc.) — NoResult, não Fail:
            // deixa o endpoint anônimo seguir normalmente quando não exige [Authorize].
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }
}
