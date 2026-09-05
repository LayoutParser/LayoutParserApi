using System.Security.Claims;

namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Mapeia a App Role do token Entra (claim <c>roles</c>) para a role interna que o resto do
    /// pipeline de autorização já entende (<c>ClaimTypes.Role</c>), sem duplicar lógica de
    /// autorização — ADR M2M, "Escopo/role mínimo — nunca admin".
    /// </summary>
    /// <remarks>
    /// <para>Escopo deliberadamente mínimo: só <see cref="ServiceE2EAppRole"/> é mapeada. Qualquer
    /// endpoint que exija <c>[Authorize(Roles = "admin")]</c> não aceita esta identidade — ampliar
    /// o escopo exige decisão nova registrada, não herança automática (mesma trava já desenhada
    /// para a Opção B descartada, reaproveitada aqui).</para>
    /// <para>Função pura sobre <see cref="ClaimsPrincipal"/> — testável sem subir o pipeline JWT
    /// Bearer inteiro, no mesmo espírito de <c>RoleAuthorizationTests</c> (sem
    /// <c>WebApplicationFactory</c> neste projeto de testes).</para>
    /// </remarks>
    public static class ServiceClientRoleMapper
    {
        /// <summary>Claim type que o Entra usa para App Roles (application permissions).</summary>
        public const string AppRoleClaimType = "roles";

        /// <summary>Nome da App Role no Entra, exposta como application permission.</summary>
        public const string ServiceE2EAppRole = "Service.E2E";

        /// <summary>Role interna equivalente, mesmo nome que a Opção B (descartada) já desenhava.</summary>
        public const string ServiceE2EInternalRole = "servico-e2e";

        /// <summary>
        /// Traduz uma App Role do Entra para a role interna correspondente. Retorna <c>null</c>
        /// para qualquer valor não reconhecido (inclusive vazio) — nunca mapeia para um papel
        /// amplo por acidente.
        /// </summary>
        public static string? MapAppRole(string? appRole) =>
            appRole == ServiceE2EAppRole ? ServiceE2EInternalRole : null;

        /// <summary>
        /// Lê todas as claims <c>roles</c> do principal validado e adiciona a claim
        /// <see cref="ClaimTypes.Role"/> interna correspondente, para cada uma reconhecida.
        /// Não lança, não faz nada se o principal não tiver <see cref="ClaimsIdentity"/> gravável.
        /// </summary>
        public static void MapAppRolesToInternalRoles(ClaimsPrincipal? principal)
        {
            if (principal?.Identity is not ClaimsIdentity identity)
                return;

            foreach (var claim in principal.FindAll(AppRoleClaimType).ToList())
            {
                var internalRole = MapAppRole(claim.Value);
                if (internalRole != null && !principal.HasClaim(ClaimTypes.Role, internalRole))
                    identity.AddClaim(new Claim(ClaimTypes.Role, internalRole));
            }
        }
    }
}
