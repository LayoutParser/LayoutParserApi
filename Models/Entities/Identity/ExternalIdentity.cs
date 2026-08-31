namespace LayoutParserApi.Models.Entities.Identity
{
    /// <summary>
    /// Vínculo entre um provedor de identidade externo (Entra/Google/etc.) e o <see cref="UserId"/>
    /// interno. Chave lógica única: (<see cref="Provider"/>, <see cref="TenantOrIssuer"/>,
    /// <see cref="Subject"/>). O <see cref="Subject"/> (equivalente ao <c>sub</c> do OIDC) NUNCA deve
    /// aparecer em log nem voltar ao navegador — só o <see cref="UserId"/> interno é observável fora
    /// da resolução (ver .claude/rules/security.md).
    /// </summary>
    public class ExternalIdentity
    {
        public Guid ExternalIdentityId { get; set; }

        public Guid UserId { get; set; }

        public string Provider { get; set; } = string.Empty;

        public string TenantOrIssuer { get; set; } = string.Empty;

        public string Subject { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
