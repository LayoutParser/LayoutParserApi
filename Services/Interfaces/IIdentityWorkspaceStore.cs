namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Resumo de um workspace + o papel do usuário nele — forma de retorno de
    /// <see cref="IIdentityWorkspaceStore"/> e do corpo de <c>GET /api/workspaces/me</c> /
    /// <c>GET /api/workspaces/{workspaceId}</c> (contrato cross-repo
    /// <c>fiscal-workspace-and-mapping-explanation-api.md</c> §2).
    /// </summary>
    public sealed record WorkspaceSummary(
        Guid WorkspaceId,
        string Name,
        string Kind,
        string Role,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// Camada de persistência crua (SQL) da identidade/workspace fiscal — Scoped, sem lógica de
    /// negócio além de garantir idempotência ao nível do banco (UNIQUE constraint + retry em
    /// violação). <see cref="Services.Identity.IdentityWorkspaceService"/> é quem orquestra e aplica a
    /// política fail-closed; esta interface só existe para permitir dublê (fake) nos testes de
    /// isolamento sem depender de SQL Server real.
    /// </summary>
    public interface IIdentityWorkspaceStore
    {
        /// <summary>
        /// Resolve o <see cref="Models.Entities.Identity.ExternalIdentity"/> único (provider, tenant,
        /// subject) para um <c>UserId</c> interno, criando o <c>FiscalUser</c>/<c>ExternalIdentity</c>
        /// na primeira resolução. Idempotente sob concorrência (UNIQUE constraint no SQL — ver
        /// <c>SqlIdentityWorkspaceStore</c>).
        /// </summary>
        Task<Guid> ResolveOrCreateUserAsync(string provider, string tenantOrIssuer, string subject, CancellationToken cancellationToken);

        /// <summary>
        /// Garante que <paramref name="userId"/> tem um workspace pessoal, criando-o de forma
        /// idempotente se ainda não existir.
        /// </summary>
        Task<WorkspaceSummary> EnsurePersonalWorkspaceAsync(Guid userId, CancellationToken cancellationToken);

        /// <summary>Todos os workspaces em que <paramref name="userId"/> é membro.</summary>
        Task<IReadOnlyList<WorkspaceSummary>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken);

        /// <summary>
        /// Retorna o workspace SÓ se <paramref name="userId"/> for membro; caso contrário
        /// <c>null</c> — o chamador (controller) traduz ambos os casos ("não existe" e "existe mas
        /// não é seu") no MESMO 404, para não permitir enumeração.
        /// </summary>
        Task<WorkspaceSummary?> GetWorkspaceIfMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken);
    }
}
