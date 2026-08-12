namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Núcleo puro da extração de identidade: a decisão de confiança e o parse do CSV de papéis.
    /// Separado do <c>TrustedIdentityMiddleware</c> pelo mesmo motivo de <c>LowCodeCandidatesBudget</c>
    /// — o middleware depende de <c>HttpContext</c>; esta classe é pura e é exatamente a parte que
    /// precisa de teste, porque uma decisão de confiança frouxa abre a porta sem ninguém notar.
    /// </summary>
    public static class TrustedIdentityPolicy
    {
        /// <summary>
        /// 🔴 A GUARDA, em forma pura. Só confia nos headers de identidade quando a origem é confiável.
        /// Com <paramref name="trustLoopbackOnly"/> ligado (default de produção), confiar exige
        /// <paramref name="isLoopback"/> — o salto do BFF co-hospedado. Fora de loopback, retorna
        /// <c>false</c> e os headers são ignorados por completo.
        /// </summary>
        public static bool ShouldTrust(bool isLoopback, bool trustLoopbackOnly)
            => !trustLoopbackOnly || isLoopback;

        /// <summary>
        /// Parseia o CSV de papéis (<c>x-iis-roles</c>): separa por vírgula, apara espaços e descarta
        /// entradas vazias. <c>null</c>/vazio → lista vazia, nunca lança.
        /// </summary>
        public static IReadOnlyList<string> ParseRoles(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return Array.Empty<string>();

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
