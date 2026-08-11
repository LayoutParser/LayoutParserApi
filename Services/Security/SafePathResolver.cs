using System.Text.RegularExpressions;

namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Resolve com segurança um nome vindo do cliente contra um diretório base, barrando
    /// <b>path traversal</b> (caminho enraizado, separador de caminho, <c>..</c> e <c>:</c>).
    ///
    /// <para>Motivo de existir (P0 de docs/architecture/plano-seguranca-arquitetura-2026-08.md):
    /// <c>Path.Combine(base, x)</c> <b>DESCARTA</b> o <c>base</c> quando <c>x</c> é um caminho
    /// enraizado (<c>C:\...</c>). Foi como <c>GET /api/document/layout/C:%5CWindows%5Cwin.ini</c>
    /// devolveu o <c>win.ini</c> real com 200. O <c>../</c> já era barrado pela normalização de URL;
    /// o caminho absoluto passava direto.</para>
    ///
    /// <para><b>Duas camadas — nenhuma sozinha basta:</b></para>
    /// <list type="number">
    ///   <item>LISTA BRANCA de caractere: o nome tem que casar <c>^[A-Za-z0-9._-]+$</c> e não conter
    ///     <c>..</c>. É <b>validar, não sanear</b> — entrada suspeita é RECUSADA, não "consertada"
    ///     removendo caractere.</item>
    ///   <item>CANONICALIZAÇÃO: mesmo passando (1), confere que <c>Path.GetFullPath(base + nome)</c>
    ///     continua <b>dentro</b> do base canonicalizado (com separador final, para não casar
    ///     <c>…\LayoutX</c> com <c>…\Layout</c>). Fecha qualquer brecha de normalização do SO.</item>
    /// </list>
    ///
    /// <para>Extraído como helper único, e não copiado em cada controller, de propósito: a correção
    /// não pode divergir entre os pontos que a usam (DocumentController, MetricsController,
    /// ParseController).</para>
    /// </summary>
    public static class SafePathResolver
    {
        // Ancorada e sem classe que case separador. Compilada uma vez (uso quente em endpoint).
        private static readonly Regex NomeValido = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

        /// <summary>
        /// Devolve o caminho absoluto seguro dentro de <paramref name="baseDir"/>, ou <c>null</c> se
        /// o nome for recusado (camada 1) ou escapar do base (camada 2).
        ///
        /// <para><b>Nunca lança</b> por entrada malformada — <c>null</c> É a recusa. O chamador
        /// responde <c>404</c> (não <c>400</c>: não revela se o arquivo existe).</para>
        /// </summary>
        /// <param name="baseDir">Diretório base já conhecido pela aplicação (não vem do cliente).</param>
        /// <param name="clientName">Nome de arquivo/diretório cru vindo do cliente.</param>
        public static string? Resolve(string baseDir, string clientName)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(clientName))
                return null;

            // Camada 1 — lista branca + recusa explícita de "..".
            // O "Contains" é necessário porque a regex permite '.', então ".." casaria a lista branca.
            if (clientName.Contains("..") || !NomeValido.IsMatch(clientName))
                return null;

            // Camada 2 — canonicaliza e confere contenção.
            string baseCanon;
            string fullCanon;
            try
            {
                baseCanon = Path.GetFullPath(baseDir);
                fullCanon = Path.GetFullPath(Path.Combine(baseCanon, clientName));
            }
            catch
            {
                // Qualquer nome que exploda a canonicalização é tratado como recusa, não como erro.
                return null;
            }

            var baseComSep = baseCanon.EndsWith(Path.DirectorySeparatorChar)
                ? baseCanon
                : baseCanon + Path.DirectorySeparatorChar;

            // Windows é case-insensitive no filesystem; a comparação de contenção acompanha.
            if (!fullCanon.StartsWith(baseComSep, StringComparison.OrdinalIgnoreCase))
                return null;

            return fullCanon;
        }

        /// <summary>
        /// Confere que <paramref name="candidatePath"/> (já montado) está <b>dentro</b> de
        /// <paramref name="baseDir"/> canonicalizado. Para o caso em que a lista branca estrita não
        /// serve — nome de arquivo de upload real carrega espaço, parêntese etc. — mas ainda assim
        /// não pode escapar da base. Combine com <c>Path.GetFileName</c> antes de montar o caminho.
        /// </summary>
        public static bool IsInsideBase(string baseDir, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(candidatePath))
                return false;

            try
            {
                var baseCanon = Path.GetFullPath(baseDir);
                var candCanon = Path.GetFullPath(candidatePath);
                var baseComSep = baseCanon.EndsWith(Path.DirectorySeparatorChar)
                    ? baseCanon
                    : baseCanon + Path.DirectorySeparatorChar;
                return candCanon.StartsWith(baseComSep, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
