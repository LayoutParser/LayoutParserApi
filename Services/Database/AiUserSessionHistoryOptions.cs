namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Configuração de retenção do histórico de longo prazo do pathway de IA por usuário
    /// (<c>tbLpAiUserSessionHistoryEntry</c>, issue #102). Fecha o gap apontado na issue #97:
    /// TTL/retenção "no mesmo espírito" do já aplicado ao <c>AiCandidateStore</c> (issue #51,
    /// ver <see cref="Transformation.Ai.AiTransformationCandidateOptions"/>), sem sem o qual o
    /// histórico cresce indefinidamente.
    /// </summary>
    public class AiUserSessionHistoryOptions
    {
        /// <summary>
        /// Retenção default do histórico, em dias. Diferente do TTL curto do
        /// <c>AiCandidateStore</c> (72h — cache quente de um job em progresso), esta tabela é o
        /// próprio histórico de auditoria de longo prazo (issue #102 a chama de "histórico de longo
        /// prazo"), então a janela é maior: 180 dias cobre ~6 meses de rastreabilidade sem crescer
        /// para sempre.
        /// </summary>
        public const int DefaultHistoryRetentionDays = 180;

        /// <summary>Intervalo default entre varreduras de purga (mesmo espírito de <see cref="Transformation.Ai.AiTransformationCandidateOptions.DefaultCleanupIntervalMinutes"/>, só que mais espaçado — não é cache quente).</summary>
        public const int DefaultCleanupIntervalMinutes = 360; // 6h

        /// <summary>
        /// Retenção do histórico, em dias. Valor &lt;= 0 cai no default
        /// (<see cref="DefaultHistoryRetentionDays"/>), mesma convenção do
        /// <c>AiTransformationCandidateOptions.TicketTtlHours</c>.
        /// </summary>
        public int HistoryRetentionDays { get; set; } = DefaultHistoryRetentionDays;

        /// <summary>
        /// Intervalo entre varreduras do <see cref="AiUserSessionHistoryCleanupBackgroundService"/>,
        /// em minutos. Valor &lt;= 0 cai no default (<see cref="DefaultCleanupIntervalMinutes"/>).
        /// </summary>
        public int CleanupIntervalMinutes { get; set; } = DefaultCleanupIntervalMinutes;
    }
}
