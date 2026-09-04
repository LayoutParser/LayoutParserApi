namespace LayoutParserApi.Services.Security
{
    /// <summary>Nomes fixos dos esquemas de autenticação envolvidos no roteamento "smart" (ADR M2M).</summary>
    public static class ServiceClientAuthenticationDefaults
    {
        /// <summary>Nome do <c>AuthenticationScheme</c> JWT Bearer (M2M, client credentials Entra).</summary>
        public const string SchemeName = "ServiceClient";
    }

    /// <summary>Nome do esquema "roteador" que decide, por requisição, entre <c>TrustedHeader</c>
    /// (identidade do BFF, por rede/loopback) e <see cref="ServiceClientAuthenticationDefaults.SchemeName"/>
    /// (identidade M2M, por token JWT assinado) — sem exigir <c>AuthenticationSchemes=</c> explícito
    /// em cada <c>[Authorize]</c>.</summary>
    public static class SmartAuthenticationDefaults
    {
        public const string SchemeName = "SmartAuth";
    }
}
