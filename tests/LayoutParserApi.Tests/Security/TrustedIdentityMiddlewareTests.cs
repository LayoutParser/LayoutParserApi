using System.Net;

using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Security;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// Trava a GUARDA DE LOOPBACK do <see cref="TrustedIdentityMiddleware"/> — o que impede um host
    /// remoto de forjar <c>x-iis-user: admin</c> direto na API mesmo com ela em <c>0.0.0.0</c>.
    ///
    /// <para>O teste que mais importa é <see cref="Origem_nao_loopback_ignora_os_headers"/>: se alguém
    /// remover a guarda (passar a confiar sempre), ele fica VERMELHO. Sem ele, a proteção pode sumir
    /// sem ninguém ver.</para>
    /// </summary>
    public class TrustedIdentityMiddlewareTests
    {
        private const string UserHeader = "x-iis-user";
        private const string RolesHeader = "x-iis-roles";

        [Fact]
        public async Task Loopback_com_headers_produz_identidade_com_papel()
        {
            var currentUser = await ExecutarAsync(
                remoteIp: IPAddress.Loopback,
                user: "alice",
                roles: "admin,operador");

            Assert.True(currentUser.IsAuthenticated);
            Assert.Equal("alice", currentUser.Name);
            Assert.Equal(new[] { "admin", "operador" }, currentUser.Roles);
            Assert.True(currentUser.IsInRole("admin"));
            Assert.True(currentUser.IsInRole("OPERADOR")); // case-insensitive
            Assert.False(currentUser.IsInRole("viewer"));
        }

        [Fact]
        public async Task Loopback_IPv6_tambem_e_confiavel()
        {
            var currentUser = await ExecutarAsync(
                remoteIp: IPAddress.IPv6Loopback,
                user: "bob",
                roles: "operador");

            Assert.True(currentUser.IsAuthenticated);
            Assert.Equal("bob", currentUser.Name);
        }

        /// <summary>
        /// 🔴 O TESTE QUE MORDE. Mesma identidade, mesma sintaxe de header — mas a conexão vem de outro
        /// host (não-loopback). Com a guarda ativa (default), os headers são IGNORADOS por completo e a
        /// identidade fica anônima. Remover a guarda (confiar sempre) quebra este teste.
        /// </summary>
        [Fact]
        public async Task Origem_nao_loopback_ignora_os_headers()
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("172.25.32.42");
            context.Request.Headers[UserHeader] = "admin";
            context.Request.Headers[RolesHeader] = "admin";

            var currentUser = new CurrentUser();
            await CriarMiddleware(new TrustedIdentityOptions()).InvokeAsync(context, currentUser);

            // Identidade anônima: os headers de um host remoto não valem nada.
            Assert.False(currentUser.IsAuthenticated);
            Assert.Null(currentUser.Name);
            Assert.Empty(currentUser.Roles);
            // O slot padrão do ASP.NET também permanece não autenticado.
            Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        }

        [Fact]
        public async Task Loopback_sem_header_fica_anonimo_sem_excecao()
        {
            var currentUser = await ExecutarAsync(
                remoteIp: IPAddress.Loopback,
                user: null,
                roles: null);

            Assert.False(currentUser.IsAuthenticated);
            Assert.Null(currentUser.Name);
            Assert.Empty(currentUser.Roles);
        }

        [Fact]
        public async Task Remote_ip_nulo_nao_e_loopback_e_fica_anonimo()
        {
            // Conexão sem IP remoto (cenário de host mal configurado) NÃO conta como loopback.
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = null;
            context.Request.Headers[UserHeader] = "admin";

            var currentUser = new CurrentUser();
            await CriarMiddleware(new TrustedIdentityOptions()).InvokeAsync(context, currentUser);

            Assert.False(currentUser.IsAuthenticated);
        }

        [Fact]
        public async Task Nome_de_usuario_e_aparado_e_papeis_parseados_com_espacos()
        {
            var currentUser = await ExecutarAsync(
                remoteIp: IPAddress.Loopback,
                user: "  alice  ",
                roles: " admin , operador ,, viewer ");

            Assert.Equal("alice", currentUser.Name);
            Assert.Equal(new[] { "admin", "operador", "viewer" }, currentUser.Roles);
        }

        [Fact]
        public async Task Nomes_de_header_customizados_sao_respeitados()
        {
            var options = new TrustedIdentityOptions
            {
                TrustedUserHeader = "x-custom-user",
                TrustedRolesHeader = "x-custom-roles"
            };

            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Headers["x-custom-user"] = "carol";
            context.Request.Headers["x-custom-roles"] = "admin";
            // Os headers default NÃO devem ser lidos quando outros nomes estão configurados.
            context.Request.Headers[UserHeader] = "intruso";

            var currentUser = new CurrentUser();
            await CriarMiddleware(options).InvokeAsync(context, currentUser);

            Assert.Equal("carol", currentUser.Name);
            Assert.True(currentUser.IsInRole("admin"));
        }

        /// <summary>
        /// Com a guarda desligada explicitamente (Security__TrustIdentityFromLoopbackOnly=false), a
        /// identidade passa a ser confiada mesmo de origem remota. Prova que a flag realmente controla
        /// a guarda — e serve de contraponto ao teste que morde.
        /// </summary>
        [Fact]
        public async Task Guarda_desligada_confia_de_origem_remota()
        {
            var options = new TrustedIdentityOptions { TrustIdentityFromLoopbackOnly = false };

            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("172.25.32.42");
            context.Request.Headers[UserHeader] = "alice";
            context.Request.Headers[RolesHeader] = "admin";

            var currentUser = new CurrentUser();
            await CriarMiddleware(options).InvokeAsync(context, currentUser);

            Assert.True(currentUser.IsAuthenticated);
            Assert.Equal("alice", currentUser.Name);
        }

        [Fact]
        public async Task Enforcement_pronto_o_slot_padrao_carrega_o_papel()
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Headers[UserHeader] = "alice";
            context.Request.Headers[RolesHeader] = "admin";

            var currentUser = new CurrentUser();
            await CriarMiddleware(new TrustedIdentityOptions()).InvokeAsync(context, currentUser);

            // HttpContext.User populado com o papel deixa um [Authorize(Roles=...)] futuro pronto.
            Assert.True(context.User.Identity?.IsAuthenticated ?? false);
            Assert.Equal("alice", context.User.Identity?.Name);
            Assert.True(context.User.IsInRole("admin"));
        }

        // --- helpers ---

        private static async Task<CurrentUser> ExecutarAsync(IPAddress? remoteIp, string? user, string? roles)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = remoteIp;
            if (user != null)
                context.Request.Headers[UserHeader] = user;
            if (roles != null)
                context.Request.Headers[RolesHeader] = roles;

            var currentUser = new CurrentUser();
            await CriarMiddleware(new TrustedIdentityOptions()).InvokeAsync(context, currentUser);
            return currentUser;
        }

        private static TrustedIdentityMiddleware CriarMiddleware(TrustedIdentityOptions options)
            => new(
                next: _ => Task.CompletedTask,
                options: Options.Create(options),
                logger: NullLogger<TrustedIdentityMiddleware>.Instance);
    }
}
