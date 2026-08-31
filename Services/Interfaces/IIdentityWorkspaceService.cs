namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Corpo de resposta de <c>GET /api/workspaces/me</c>.</summary>
    public sealed record WorkspaceMeResult(Guid ActiveWorkspaceId, IReadOnlyList<WorkspaceSummary> Workspaces);

    /// <summary>
    /// Orquestra identidade externa → <c>UserId</c> interno → workspace pessoal, aplicando a política
    /// fail-closed do Slice 1 (issue #225/#228): qualquer falha na resolução (SQL fora do ar, etc.)
    /// resulta em <c>null</c>/negação, nunca em acesso permissivo. Consumido pelo
    /// <c>TrustedIdentityMiddleware</c> (resolução) e pelo <c>WorkspacesController</c> (consulta).
    /// </summary>
    public interface IIdentityWorkspaceService
    {
        /// <summary>
        /// Resolve/cria o <c>UserId</c> a partir da identidade externa. <c>null</c> quando os dados de
        /// entrada são inválidos OU a resolução falhou (SQL indisponível) — nunca lança, para não
        /// derrubar o pipeline de middleware; o chamador trata <c>null</c> como identidade não
        /// resolvida (fail-closed: endpoints que exigem <c>UserId</c> negam acesso).
        /// </summary>
        Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken cancellationToken);

        /// <summary>
        /// Workspaces do usuário, criando o pessoal de forma idempotente se ainda não existir.
        /// Propaga exceção (o controller decide como degradar a resposta HTTP) — diferente da
        /// resolução de identidade, aqui já sabemos que o <c>UserId</c> é válido.
        /// </summary>
        Task<WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken);

        /// <summary>
        /// Workspace só se <paramref name="userId"/> for membro; <c>null</c> caso contrário (mapeado
        /// para 404 pelo controller, "não existe" e "não é seu" indistinguíveis).
        /// </summary>
        Task<WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken);
    }
}
