using LayoutParserApi.Services.Parsing.Interfaces;

using System.Text.RegularExpressions;
using System.Xml;

namespace LayoutParserApi.Services.Parsing.Implementations
{
    public class LayoutDetector : ILayoutDetector
    {
        public string DetectType(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "unknown";

            Console.WriteLine($"Detectando tipo de layout... (tamanho: {content.Length} caracteres)");

            if (LooksLikeXml(content) && IsWellFormedXml(content))
            {
                Console.WriteLine("Detectado como XML");
                return "xml";
            }

            if (LooksLikeMqSeries(content))
            {
                Console.WriteLine("Detectado como mqseries");
                return "mqseries";
            }

            if (LooksLikeIdoc(content))
            {
                Console.WriteLine("Detectado como idoc");
                return "idoc";
            }

            Console.WriteLine("Tipo desconhecido");
            return "unknown";
        }

        private bool LooksLikeXml(string content)
        {
            var trimmed = content.TrimStart();
            return trimmed.StartsWith("<") && (trimmed.Contains("<?xml") || trimmed.Contains("<NFe") || trimmed.Contains("</"));
        }

        private bool IsWellFormedXml(string content)
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreWhitespace = true };
                using var reader = XmlReader.Create(new StringReader(content), settings);
                while (reader.Read()) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool LooksLikeMqSeries(string content)
        {
            if (string.IsNullOrEmpty(content))
                return false;

            // Remover quebras de linha para análise
            var cleanContent = content.Replace("\r", "").Replace("\n", "");
            
            // Verificar se começa com HEADER
            if (!cleanContent.StartsWith("HEADER"))
                return false;

            // Verificar se o tamanho é múltiplo de 600 (linhas de 600 caracteres)
            if (cleanContent.Length < 600 || cleanContent.Length % 600 != 0)
                return false;

            // Verificar se tem padrões sequenciais típicos do mqseries
            // Formato: NNNNNNLLL onde N=sequencial (6 dígitos) e L=linha (3 dígitos)
            // Ex: 000001000, 000002001, 000003002, etc.
            var sequentialMatches = Regex.Matches(cleanContent, @"\d{6}\d{3}");

            // Deve ter pelo menos 2 sequenciais para confirmar o padrão
            if (sequentialMatches.Count < 2)
                return false;

            // Verificar se termina com linha 999 (típico do mqseries)
            var endsWithLinha999 = cleanContent.Contains("999999") || Regex.IsMatch(cleanContent, @"\d{6}999");

            // Verificar se tem múltiplas "linhas" lógicas de 600 caracteres (mínimo 2)
            int logicalLineCount = cleanContent.Length / 600;

            return logicalLineCount >= 2 && (sequentialMatches.Count >= 3 || endsWithLinha999);
        }

        private bool LooksLikeIdoc(string content)
        {
            var firstLine = GetFirstLine(content);
            if (string.IsNullOrEmpty(firstLine)) return false;

            if (firstLine.StartsWith("EDI_") || firstLine.Contains("ZRSDM_")) return true;

            var tokens = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 5 && tokens.Any(t => t.Length > 15 && IsAllDigitsOrUpper(t));
        }

        private string GetFirstLine(string content)
        {
            using var reader = new StringReader(content);
            return reader.ReadLine() ?? string.Empty;
        }

        private bool IsAllDigitsOrUpper(string s) => s.All(c => char.IsDigit(c) || (char.IsLetter(c) && char.IsUpper(c)) || c == '_' || c == '-');
    }
}