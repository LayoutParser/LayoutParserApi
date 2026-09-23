namespace LayoutParserApi.Models.Entities.Identity
{
    /// <summary>
    /// Papéis de <see cref="WorkspaceMembership"/> previstos pela auditoria do Slice 1
    /// (docs/architecture/auditoria-slice1-identidade-workspaces-2026-08-31.md §1). Só "Owner" é
    /// atribuído nesta fase (workspace pessoal); os demais existem para não quebrar o modelo quando
    /// workspaces de time (Slice 2+) chegarem.
    /// </summary>
    public static class WorkspaceRole
    {
        public const string Owner = "owner";
        public const string FiscalAdmin = "fiscal_admin";
        public const string Mapper = "mapper";
        public const string Reviewer = "reviewer";
        public const string Operator = "operator";
        public const string Viewer = "viewer";
    }

    /// <summary>
    /// Vínculo N:N entre <see cref="FiscalUser"/> e <see cref="FiscalWorkspace"/>. Um usuário pode
    /// pertencer a vários workspaces; a existência (ou não) de membership é o único critério de
    /// autorização — "não existe" e "existe mas não é seu" respondem o MESMO 404 (nunca 403), para não
    /// permitir enumeração de workspace por ID.
    /// </summary>
    public class WorkspaceMembership
    {
        public Guid WorkspaceMembershipId { get; set; }

        public Guid WorkspaceId { get; set; }

        public Guid UserId { get; set; }

        /// <summary>Ver <see cref="WorkspaceRole"/>.</summary>
        public string Role { get; set; } = WorkspaceRole.Owner;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
