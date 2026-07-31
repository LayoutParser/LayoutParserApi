namespace LayoutParserApi.Services.Logging
{
    /// <summary>
    /// Higieniza texto vindo de FORA do processo (front-end via POST api/logs/client, resultado do
    /// Cypress via POST api/ai-metrics/cypress-result) antes de ele virar linha de log.
    /// </summary>
    /// <remarks>
    /// ✅ Motivo (QA/Quinn 2026-07-30): o log é lido de volta como fonte de dados
    /// (<see cref="UnifiedLogReaderService"/> / <see cref="AiMetricsReaderService"/>), então uma
    /// quebra de linha crua no texto do usuário cria uma ENTRADA FALSA no leitor — um stack trace
    /// colado num campo de observação foi suficiente pra injetar uma "geração fantasma" com
    /// métricas arbitrárias no painel de IA (média de tokens/s saltou de 3.29 pra 501.14).
    /// Achatar CR/LF e truncar mata o vetor na origem, pros dois produtores.
    /// </remarks>
    public static class LogMessageSanitizer
    {
        /// <summary>Teto padrão de tamanho (mesmo valor já usado pela ingestão de log do front-end).</summary>
        public const int DefaultMaxLength = 4000;

        /// <summary>
        /// Achata quebras de linha (CRLF/LF/CR viram espaço) e trunca no limite informado.
        /// Entrada nula/vazia retorna string vazia — nunca lança.
        /// </summary>
        public static string Sanitize(string? value, int maxLength = DefaultMaxLength)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var flattened = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            return flattened.Length > maxLength ? flattened[..maxLength] : flattened;
        }

        /// <summary>
        /// Mesma higienização de <see cref="Sanitize"/>, preservando <c>null</c> como <c>null</c>
        /// (pra campos opcionais, onde "ausente" e "vazio" têm significados diferentes no log).
        /// </summary>
        public static string? SanitizeOptional(string? value, int maxLength = DefaultMaxLength)
            => value is null ? null : Sanitize(value, maxLength);
    }
}
