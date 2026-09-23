namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Identidade do usuário da requisição atual, injetada pelo BFF (Fastify) via headers de confiança
    /// e extraída pelo <c>TrustedIdentityMiddleware</c> sob a guarda de loopback. É o mecanismo que a
    /// API expõe a controllers, filtros e auditoria para saber <b>quem</b> está chamando.
    /// </summary>
    /// <remarks>
    /// <para>Serviço <c>Scoped</c> (uma instância por requisição). Fora de loopback, sem header, ou
    /// com a guarda ativa e origem não confiável, a identidade é <b>anônima</b> — nunca lança.</para>
    ///
    /// <para><b>Enforcement por papel está DESLIGADO de propósito.</b> Esta interface é o mecanismo
    /// pronto (papéis parseados, <see cref="IsInRole"/> disponível), mas nenhum endpoint bloqueia por
    /// papel ainda — isso é decisão de produto (quais operações são privilegiadas) e entra depois. Hoje
    /// a identidade apenas <b>flui e é auditada</b>.</para>
    /// </remarks>
    public interface ICurrentUser
    {
        /// <summary>Nome do usuário (claim vinda do BFF) ou <c>null</c> quando anônimo.</summary>
        string? Name { get; }

        /// <summary>Papéis do usuário, já parseados do CSV. Vazio quando anônimo ou sem papéis.</summary>
        IReadOnlyList<string> Roles { get; }

        /// <summary>True quando há um <see cref="Name"/> confiável associado à requisição.</summary>
        bool IsAuthenticated { get; }

        /// <summary>Confere (case-insensitive) se o usuário possui o papel informado.</summary>
        bool IsInRole(string role);

        /// <summary>
        /// Identidade imutável interna (Slice 1 — issue #225), resolvida pelo
        /// <c>IIdentityWorkspaceService</c> a partir dos headers <c>x-layoutparser-identity-*</c>.
        /// <c>null</c> quando os headers novos estão ausentes ou a resolução falhou (fail-closed:
        /// endpoints que exigem workspace negam acesso em vez de degradar). Independente de
        /// <see cref="Name"/>/<see cref="Roles"/> (legados, <c>x-iis-*</c>) — os dois convivem durante
        /// a transição.
        /// </summary>
        Guid? UserId { get; }
    }
}
