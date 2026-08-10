using System.Text.RegularExpressions;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Saneia mensagens de erro do pathway low-code ANTES de elas irem para o wire (spec §3.1).
    ///
    /// <para>Motivo concreto: o runner lançava <c>"Runner não gerou outputFile: C:\inetpub\..."</c>,
    /// essa mensagem virava <c>ErrorMessage</c> do candidato e saía no payload <b>200</b> do parse —
    /// entregando a estrutura de diretórios do servidor para o cliente. O detalhe continua no log
    /// (onde é útil para diagnóstico); no wire vai a versão sem caminho.</para>
    /// </summary>
    public static class LowCodeErrorSanitizer
    {
        /// <summary>Substituto do caminho removido — mantém a frase legível sem revelar o disco.</summary>
        public const string PathPlaceholder = "[caminho interno]";

        // Caminho absoluto do Windows (C:\..., C:/...) ou UNC (\\servidor\share\...). O runner e o
        // .NET (IOException) produzem os dois formatos. Paramos em espaço/aspas/pipe porque é onde
        // um caminho termina nas mensagens que este pathway realmente produz.
        private static readonly Regex CaminhoAbsoluto = new(
            @"(?:[A-Za-z]:[\\/]|\\\\)[^\s""'<>|]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Versão da mensagem segura para serializar. Retorna null/vazio inalterado (não inventa texto).
        /// </summary>
        public static string? ForWire(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return message;

            return CaminhoAbsoluto.Replace(message, PathPlaceholder);
        }

        /// <summary>Conveniência para o ponto mais comum: exceção capturada por candidato.</summary>
        public static string? ForWire(Exception? ex) => ForWire(ex?.Message);
    }
}
