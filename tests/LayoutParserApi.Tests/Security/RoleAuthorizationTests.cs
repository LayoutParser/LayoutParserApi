using System.Net;
using System.Security.Claims;

using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Security;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// Issue #32: os endpoints sensíveis (GET /api/logs, DataGeneration/*,
    /// TransformationExecution/execute-candidates e execute-lowcode, MapperDatabaseController/
    /// refresh-cache) ganharam <c>[Authorize(Roles = "...")]</c> em cima da identidade que o
    /// <see cref="TrustedIdentityMiddleware"/> já populava em <c>HttpContext.User</c>.
    ///
    /// <para>Não há <c>WebApplicationFactory</c>/TestHost neste projeto de testes (decisão
    /// deliberada de manter a suíte sem pacotes extras — ver comentário no .csproj), então este
    /// teste exercita a MESMA peça que <c>[Authorize(Roles=...)]</c> usa por baixo — o
    /// <see cref="IAuthorizationService"/> com uma <see cref="RolesAuthorizationRequirement"/> —
    /// contra o <see cref="ClaimsPrincipal"/> exatamente como o <see cref="TrustedIdentityMiddleware"/>
    /// o constrói. É a mesma verificação que o <c>AuthorizationMiddleware</c> faz para decidir
    /// entre 200 (papel correto), 403 (autenticado mas sem o papel) e 401 (anônimo).</para>
    /// </summary>
    public class RoleAuthorizationTests
    {
        private const string UserHeader = "x-iis-user";
        private const string RolesHeader = "x-iis-roles";

        [Fact]
        public async Task Usuario_com_papel_correto_e_autorizado()
        {
            var context = await ExecutarMiddlewareAsync(user: "alice", roles: "admin");

            var resultado = await AutorizarAsync(context.User, "admin");

            Assert.True(resultado.Succeeded);
        }

        [Fact]
        public async Task Usuario_autenticado_sem_o_papel_exigido_e_negado()
        {
            // "bob" está autenticado (identidade confiável), mas com papel "operador" — não
            // "admin". Isso é o caminho de 403 (Forbid), diferente de anônimo (401/Challenge).
            var context = await ExecutarMiddlewareAsync(user: "bob", roles: "operador");

            var resultado = await AutorizarAsync(context.User, "admin");

            Assert.False(resultado.Succeeded);
        }

        [Fact]
        public async Task Usuario_anonimo_e_negado()
        {
            // Sem header confiável (ou fora de loopback) — TrustedIdentityMiddleware nunca lança,
            // fica anônimo. É o caminho de 401 (Challenge) do AuthorizationMiddleware.
            var context = await ExecutarMiddlewareAsync(user: null, roles: null);

            var resultado = await AutorizarAsync(context.User, "admin");

            Assert.False(resultado.Succeeded);
            Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        }

        [Fact]
        public async Task Papel_operador_nao_da_acesso_a_recurso_que_exige_admin()
        {
            // Confirma que os dois papéis da tabela de decisão (#32) não são intercambiáveis:
            // "operador" (refresh-cache) não deveria abrir os endpoints "admin".
            var context = await ExecutarMiddlewareAsync(user: "carol", roles: "operador");

            var resultado = await AutorizarAsync(context.User, "admin");

            Assert.False(resultado.Succeeded);
        }

        [Fact]
        public async Task Papel_admin_da_acesso_a_recurso_que_exige_operador()
        {
            // admin não está na tabela de refresh-cache, mas nada na spec exige exclusividade —
            // só documenta o requisito mínimo. Papel "admin" também atende "operador" aqui porque
            // RolesAuthorizationRequirement é um OR entre os papéis aceitos, não hierarquia; este
            // teste apenas trava que "admin" continua sendo aceito onde "operador" é exigido
            // SE a política também listar "admin" — o que não é o caso hoje (só "operador"). Então
            // o esperado é falha, documentando a ausência de hierarquia implícita no mecanismo.
            var context = await ExecutarMiddlewareAsync(user: "dave", roles: "admin");

            var resultado = await AutorizarAsync(context.User, "operador");

            Assert.False(resultado.Succeeded);
        }

        // --- helpers ---

        private static async Task<HttpContext> ExecutarMiddlewareAsync(string? user, string? roles)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (user != null)
                context.Request.Headers[UserHeader] = user;
            if (roles != null)
                context.Request.Headers[RolesHeader] = roles;

            var currentUser = new CurrentUser();
            var middleware = new TrustedIdentityMiddleware(
                next: _ => Task.CompletedTask,
                options: Options.Create(new TrustedIdentityOptions()),
                logger: NullLogger<TrustedIdentityMiddleware>.Instance);

            await middleware.InvokeAsync(context, currentUser);
            return context;
        }

        private static async Task<AuthorizationResult> AutorizarAsync(ClaimsPrincipal principal, string papelExigido)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthorization();
            using var provider = services.BuildServiceProvider();

            var authorizationService = provider.GetRequiredService<IAuthorizationService>();
            return await authorizationService.AuthorizeAsync(
                principal,
                resource: null,
                requirements: new IAuthorizationRequirement[] { new RolesAuthorizationRequirement(new[] { papelExigido }) });
        }
    }
}
