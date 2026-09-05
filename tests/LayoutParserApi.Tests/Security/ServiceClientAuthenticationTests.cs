using System.Security.Claims;

using LayoutParserApi.Services.Security;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// ADR M2M (docs/architecture/adr-autenticacao-m2m-e2e-cypress-2026-09-03.md), Parte 1:
    /// client credentials OAuth2 via Entra, esquema JWT Bearer "ServiceClient" PARALELO ao
    /// <see cref="TrustedIdentityMiddleware"/>/<see cref="TrustedHeaderAuthenticationHandler"/>
    /// existente.
    ///
    /// <para>Mesma decisão de projeto de <c>RoleAuthorizationTests</c>: sem
    /// <c>WebApplicationFactory</c>/TestHost neste projeto de testes. As duas peças novas
    /// (<see cref="SmartAuthSchemeSelector"/> e <see cref="ServiceClientRoleMapper"/>) foram
    /// desenhadas como funções puras exatamente para serem testáveis sem subir o pipeline JWT
    /// Bearer/HTTP inteiro — cobrem, respectivamente, "qual esquema autentica esta requisição" e
    /// "qual role interna uma App Role do Entra vira".</para>
    /// </summary>
    public class ServiceClientAuthenticationTests
    {
        // --- SmartAuthSchemeSelector: roteamento entre TrustedHeader e ServiceClient ---

        [Fact]
        public void Bearer_token_com_esquema_M2M_configurado_roteia_para_ServiceClient()
        {
            var esquema = SmartAuthSchemeSelector.Select("Bearer eyJhbGciOi...", serviceClientConfigured: true);

            Assert.Equal(ServiceClientAuthenticationDefaults.SchemeName, esquema);
        }

        [Fact]
        public void Bearer_token_case_insensitive_tambem_roteia_para_ServiceClient()
        {
            // RFC 7235 não exige exatamente "Bearer" — o handler HTTP real também não é
            // case-sensitive; o seletor não deveria ser mais rígido que o próprio protocolo.
            var esquema = SmartAuthSchemeSelector.Select("bearer eyJhbGciOi...", serviceClientConfigured: true);

            Assert.Equal(ServiceClientAuthenticationDefaults.SchemeName, esquema);
        }

        [Fact]
        public void Sem_header_Authorization_roteia_para_TrustedHeader()
        {
            var esquema = SmartAuthSchemeSelector.Select(authorizationHeader: null, serviceClientConfigured: true);

            Assert.Equal(TrustedHeaderAuthenticationHandler.SchemeName, esquema);
        }

        [Fact]
        public void Header_Authorization_nao_Bearer_roteia_para_TrustedHeader()
        {
            var esquema = SmartAuthSchemeSelector.Select("Basic dXNlcjpwYXNz", serviceClientConfigured: true);

            Assert.Equal(TrustedHeaderAuthenticationHandler.SchemeName, esquema);
        }

        [Fact]
        public void Bearer_token_mas_esquema_M2M_nao_configurado_ainda_roteia_para_TrustedHeader()
        {
            // Cenário real hoje: App Registration no Entra ainda não existe (dependência externa,
            // ver ADR "Plano de implementação", passo 1). Nesse estado, IsConfigured é false e o
            // request cai em TrustedHeader mesmo trazendo um Bearer — que aí falha por lá
            // (identidade anônima), nunca é uma exceção não tratada.
            var esquema = SmartAuthSchemeSelector.Select("Bearer eyJhbGciOi...", serviceClientConfigured: false);

            Assert.Equal(TrustedHeaderAuthenticationHandler.SchemeName, esquema);
        }

        // --- ServiceClientRoleMapper: App Role do Entra -> role interna ---

        [Fact]
        public void MapAppRole_Service_E2E_mapeia_para_servico_e2e()
        {
            var papelInterno = ServiceClientRoleMapper.MapAppRole("Service.E2E");

            Assert.Equal("servico-e2e", papelInterno);
            Assert.Equal(ServiceClientRoleMapper.ServiceE2EInternalRole, papelInterno);
        }

        [Theory]
        [InlineData("admin")]
        [InlineData("Service.Admin")]
        [InlineData("")]
        [InlineData(null)]
        public void MapAppRole_qualquer_valor_nao_reconhecido_nao_mapeia(string? appRole)
        {
            // Trava central do ADR: escopo mínimo, nunca herda "admin" por acidente. Qualquer App
            // Role diferente de "Service.E2E" (inclusive vazia/nula) não vira role interna nenhuma.
            var papelInterno = ServiceClientRoleMapper.MapAppRole(appRole);

            Assert.Null(papelInterno);
        }

        [Fact]
        public void MapAppRolesToInternalRoles_adiciona_claim_de_role_interna_ao_principal()
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim(ServiceClientRoleMapper.AppRoleClaimType, ServiceClientRoleMapper.ServiceE2EAppRole) },
                authenticationType: "ServiceClient");
            var principal = new ClaimsPrincipal(identity);

            ServiceClientRoleMapper.MapAppRolesToInternalRoles(principal);

            Assert.True(principal.HasClaim(ClaimTypes.Role, ServiceClientRoleMapper.ServiceE2EInternalRole));
        }

        [Fact]
        public void MapAppRolesToInternalRoles_ignora_App_Role_nao_reconhecida()
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim(ServiceClientRoleMapper.AppRoleClaimType, "Service.Admin") },
                authenticationType: "ServiceClient");
            var principal = new ClaimsPrincipal(identity);

            ServiceClientRoleMapper.MapAppRolesToInternalRoles(principal);

            Assert.False(principal.HasClaim(ClaimTypes.Role, ServiceClientRoleMapper.ServiceE2EInternalRole));
            Assert.Empty(principal.FindAll(ClaimTypes.Role));
        }

        [Fact]
        public void MapAppRolesToInternalRoles_nunca_lanca_com_principal_nulo()
        {
            var exception = Record.Exception(() => ServiceClientRoleMapper.MapAppRolesToInternalRoles(null));

            Assert.Null(exception);
        }

        // --- Fim-a-fim (sem HTTP real): principal do ServiceClient com role mapeada autoriza
        //     [Authorize] sem Roles= (o caso real de execute-lowcode hoje) e é rejeitado por
        //     [Authorize(Roles = "admin")] (trava de escopo mínimo). ---

        [Fact]
        public async Task Principal_ServiceClient_com_Service_E2E_autentica_para_Authorize_sem_Roles()
        {
            var principal = ConstruirPrincipalServiceClient(ServiceClientRoleMapper.ServiceE2EAppRole);

            Assert.True(principal.Identity?.IsAuthenticated);

            var resultado = await AutorizarAsync(principal, requirements: new IAuthorizationRequirement[] { new DenyAnonymousAuthorizationRequirement() });

            Assert.True(resultado.Succeeded);
        }

        [Fact]
        public async Task Principal_ServiceClient_com_Service_E2E_nao_atende_Authorize_Roles_admin()
        {
            // Trava de escopo mínimo (ADR, "Escopo/role mínimo — nunca admin"): a role
            // "servico-e2e" nunca deve satisfazer um endpoint que exija "admin".
            var principal = ConstruirPrincipalServiceClient(ServiceClientRoleMapper.ServiceE2EAppRole);

            var resultado = await AutorizarAsync(principal, requirements: new IAuthorizationRequirement[] { new RolesAuthorizationRequirement(new[] { "admin" }) });

            Assert.False(resultado.Succeeded);
        }

        [Fact]
        public async Task Token_sem_claim_roles_reconhecida_fica_autenticado_mas_sem_papel_nenhum()
        {
            // "Autenticado" (assinatura válida) não é o mesmo que "com o papel certo" — um token
            // Entra válido mas emitido para uma App Role diferente ainda passa em [Authorize]
            // simples (só exige identidade), mas nunca em [Authorize(Roles=...)].
            var principal = ConstruirPrincipalServiceClient(appRole: "Service.Outro");

            var semRoles = await AutorizarAsync(principal, requirements: new IAuthorizationRequirement[] { new DenyAnonymousAuthorizationRequirement() });
            var comRoleExigida = await AutorizarAsync(principal, requirements: new IAuthorizationRequirement[] { new RolesAuthorizationRequirement(new[] { "servico-e2e" }) });

            Assert.True(semRoles.Succeeded);
            Assert.False(comRoleExigida.Succeeded);
        }

        // --- helpers ---

        private static ClaimsPrincipal ConstruirPrincipalServiceClient(string appRole)
        {
            // Simula o que o JwtBearerEvents.OnTokenValidated faz: token validado (identity com
            // authenticationType não-vazio = autenticado) + claim "roles" do Entra, e então o
            // ServiceClientRoleMapper roda por cima, exatamente como em Program.cs.
            var identity = new ClaimsIdentity(
                new[] { new Claim(ServiceClientRoleMapper.AppRoleClaimType, appRole) },
                authenticationType: "ServiceClient");
            var principal = new ClaimsPrincipal(identity);

            ServiceClientRoleMapper.MapAppRolesToInternalRoles(principal);
            return principal;
        }

        private static async Task<AuthorizationResult> AutorizarAsync(ClaimsPrincipal principal, IEnumerable<IAuthorizationRequirement> requirements)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthorization();
            using var provider = services.BuildServiceProvider();

            var authorizationService = provider.GetRequiredService<IAuthorizationService>();
            return await authorizationService.AuthorizeAsync(principal, resource: null, requirements: requirements);
        }
    }
}
