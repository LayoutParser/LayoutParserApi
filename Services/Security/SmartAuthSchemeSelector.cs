namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Decide, por requisição, qual <c>AuthenticationScheme</c> deve autenticar — sem exigir
    /// <c>AuthenticationSchemes=</c> explícito em cada <c>[Authorize]</c> (ADR M2M, "Sem mudança
    /// nos <c>[Authorize]</c> existentes").
    /// </summary>
    /// <remarks>
    /// Função pura (string → string), extraída do <c>ForwardDefaultSelector</c> de um
    /// <c>AddPolicyScheme</c> em <c>Program.cs</c> só para ser testável sem subir o pipeline HTTP
    /// inteiro (mesmo espírito de <c>ServiceClientRoleMapper</c>).
    /// </remarks>
    public static class SmartAuthSchemeSelector
    {
        /// <summary>
        /// Requisição com <c>Authorization: Bearer ...</c> e o esquema M2M configurado → tenta o
        /// esquema <see cref="ServiceClientAuthenticationDefaults.SchemeName"/> (JWT Bearer,
        /// prova por criptografia). Qualquer outro caso (sem header, header não-Bearer, ou esquema
        /// M2M ainda não configurado — App Registration pendente) → cai no
        /// <see cref="TrustedHeaderAuthenticationHandler.SchemeName"/> (identidade do BFF, prova
        /// por rede/loopback), que já degrada para anônimo sozinho quando não há identidade
        /// confiável.
        /// </summary>
        public static string Select(string? authorizationHeader, bool serviceClientConfigured)
        {
            if (serviceClientConfigured
                && !string.IsNullOrEmpty(authorizationHeader)
                && authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return ServiceClientAuthenticationDefaults.SchemeName;
            }

            return TrustedHeaderAuthenticationHandler.SchemeName;
        }
    }
}
