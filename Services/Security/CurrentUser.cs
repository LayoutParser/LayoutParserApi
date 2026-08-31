using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Implementação <c>Scoped</c> de <see cref="ICurrentUser"/>. Nasce anônima; o
    /// <c>TrustedIdentityMiddleware</c> a preenche (via <see cref="Set"/>) uma única vez, no início da
    /// requisição, quando a guarda de loopback autoriza. Fora disso permanece anônima.
    /// </summary>
    public sealed class CurrentUser : ICurrentUser
    {
        public string? Name { get; private set; }

        public IReadOnlyList<string> Roles { get; private set; } = Array.Empty<string>();

        public bool IsAuthenticated => !string.IsNullOrEmpty(Name);

        public Guid? UserId { get; private set; }

        public bool IsInRole(string role)
        {
            if (string.IsNullOrEmpty(role))
                return false;

            foreach (var r in Roles)
            {
                if (string.Equals(r, role, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Preenche a identidade da requisição. <c>internal</c> de propósito: só o middleware do mesmo
        /// assembly popula; controllers e filtros só leem via <see cref="ICurrentUser"/>.
        /// </summary>
        internal void Set(string name, IReadOnlyList<string> roles)
        {
            Name = name;
            Roles = roles ?? Array.Empty<string>();
        }

        /// <summary>
        /// Preenche a identidade imutável (Slice 1). <c>internal</c> pelo mesmo motivo de
        /// <see cref="Set"/>: só o middleware do mesmo assembly popula. Aditivo — não interfere em
        /// <see cref="Name"/>/<see cref="Roles"/>, que continuam vindo (ou não) dos headers legados.
        /// </summary>
        internal void SetUserId(Guid userId)
        {
            UserId = userId;
        }
    }
}
