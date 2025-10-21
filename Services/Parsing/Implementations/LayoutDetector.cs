using LayoutParserApi.Services.Parsing.Interfaces;

using System.Xml;

namespace LayoutParserApi.Services.Parsing.Implementations
{
    public class LayoutDetector : ILayoutDetector
    {
        public string DetectType(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "unknown";

            if (LooksLikeXml(content) && IsWellFormedXml(content))
                return "xml";

            if (LooksLikeMqSeries(content))
                return "mqseries";

            if (LooksLikeIdoc(content))
                return "idoc";

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

            var firstLine = GetFirstLine(content);
            if (string.IsNullOrEmpty(firstLine))
                return false;

            if (!firstLine.StartsWith("HEADER"))
                return false;

            if (firstLine.Length < 6)
                return false;

            var cleanContent = content.Replace("\r", "").Replace("\n", "");
            bool isDivisibleBy600 = cleanContent.Length % 600 == 0;

            int lineCount = CountDocumentLines(content);

            return isDivisibleBy600 && lineCount > 1;
        }

        private bool LooksLikeIdoc(string content)
        {
            var firstLine = GetFirstLine(content);
            if (string.IsNullOrEmpty(firstLine)) return false;

            if (firstLine.StartsWith("EDI_") || firstLine.Contains("ZRSDM_")) return true;

            var tokens = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 5 && tokens.Any(t => t.Length > 15 && IsAllDigitsOrUpper(t));
        }

        private int CountDocumentLines(string content)
        {
            if (string.IsNullOrEmpty(content))
                return 0;

            return content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private string GetFirstLine(string content)
        {
            using var reader = new StringReader(content);
            return reader.ReadLine() ?? string.Empty;
        }

        private bool IsAllDigitsOrUpper(string s) => s.All(c => char.IsDigit(c) || (char.IsLetter(c) && char.IsUpper(c)) || c == '_' || c == '-');
    }
}


