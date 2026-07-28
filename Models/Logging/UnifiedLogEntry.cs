namespace LayoutParserApi.Models.Logging
{
    /// <summary>
    /// Entrada de log já parseada e normalizada, vinda de uma das fontes do ecossistema
    /// (Backend/Frontend no layoutparserapi.log, Lib, Decrypt), usada pelo endpoint unificado
    /// de leitura (GET api/logs).
    /// </summary>
    public class UnifiedLogEntry
    {
        /// <summary>Origem da entrada: Backend, Frontend, Lib ou Decrypt.</summary>
        public string Source { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }

        /// <summary>Nível como aparece no arquivo (ex.: INF, WRN, ERR, DBG).</summary>
        public string Level { get; set; } = string.Empty;

        public string? CorrelationId { get; set; }

        /// <summary>
        /// Mensagem completa da entrada, incluindo eventuais linhas de continuação
        /// (ex.: stack trace multi-linha) anexadas durante o parse.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
