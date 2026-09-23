namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Retenção do histórico de análises fiscais (issue #366). TTL de 90 dias confirmado pelo dono em
    /// 2026-09-21 (LGPD: minimização e limitação de armazenamento de dado fiscal de cliente). Seção de
    /// config: <c>FiscalAnalysisHistory</c>.
    /// </summary>
    public class FiscalAnalysisHistoryOptions
    {
        public const int DefaultRetentionDays = 90;
        public const int DefaultCleanupIntervalMinutes = 360; // 6h — mesma cadência do histórico de sessão de IA.

        /// <summary>Retenção em dias. Valor &lt;= 0 cai no default (90).</summary>
        public int RetentionDays { get; set; } = DefaultRetentionDays;

        /// <summary>Intervalo entre varreduras de purga, em minutos. Valor &lt;= 0 cai no default.</summary>
        public int CleanupIntervalMinutes { get; set; } = DefaultCleanupIntervalMinutes;

        public int EffectiveRetentionDays => RetentionDays > 0 ? RetentionDays : DefaultRetentionDays;
    }
}
