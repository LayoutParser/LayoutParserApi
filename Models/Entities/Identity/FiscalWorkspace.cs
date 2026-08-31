namespace LayoutParserApi.Models.Entities.Identity
{
    /// <summary>Tipo de workspace fiscal.</summary>
    public static class WorkspaceKind
    {
        /// <summary>Workspace pessoal, criado idempotentemente no primeiro acesso do usuário.</summary>
        public const string Personal = "personal";

        /// <summary>Workspace de time — fora do escopo do Slice 1 (só o modelo já prevê).</summary>
        public const string Team = "team";
    }

    /// <summary>
    /// Contêiner de isolamento de dado fiscal (Slice 1 — issue #225/#228). Toda leitura/escrita futura
    /// de recurso fiscal (projetos, pacotes de mapeamento, drafts) precisa validar
    /// <see cref="WorkspaceMembership"/> no servidor antes de tocar o dado — fail-closed.
    /// </summary>
    public class FiscalWorkspace
    {
        public Guid WorkspaceId { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>Ver <see cref="WorkspaceKind"/>.</summary>
        public string Kind { get; set; } = WorkspaceKind.Personal;

        public Guid OwnerUserId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
