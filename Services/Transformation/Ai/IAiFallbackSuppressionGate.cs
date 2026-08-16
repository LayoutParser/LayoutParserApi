namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Circuito de proteção do fallback automático de IA no Estado A (§5 do desenho
    /// docs/architecture/design-fallback-ia-automatico-2026-08-16.md). Diferente da
    /// <see cref="AiCandidateStore"/> (particionada por usuário+ticket), este gate é
    /// deliberadamente CROSS-USUÁRIO — a chave é só o <c>LayoutGuid</c>, porque a causa raiz
    /// ("layout sem transformação modelável") é a mesma para qualquer usuário/documento.
    /// </summary>
    public interface IAiFallbackSuppressionGate
    {
        /// <summary>
        /// Verifica se o layout está em cooldown (tentativa recente sem sucesso). Quando
        /// <c>true</c>, <paramref name="retryAt"/> traz o instante em que a próxima tentativa é
        /// permitida — usado para compor o warning "IA suprimida até HH:mm".
        /// </summary>
        bool IsInCooldown(Guid layoutGuid, out DateTimeOffset retryAt);

        /// <summary>Registra falha/esgotamento de iterações — inicia (ou renova) o cooldown.</summary>
        void RegisterFailure(Guid layoutGuid, TimeSpan cooldown);

        /// <summary>Limpa o cooldown quando o fallback converge — próxima tentativa não fica presa.</summary>
        void ClearCooldown(Guid layoutGuid);
    }
}
