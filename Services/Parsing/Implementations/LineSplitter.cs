using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Interfaces;

namespace LayoutParserApi.Services.Parsing.Implementations
{
    public class LineSplitter : ILineSplitter
    {
        private readonly ITechLogger _techLogger;

        public LineSplitter(ITechLogger techLogger)
        {
            _techLogger = techLogger;
        }

        public string[] SplitTextIntoLines(string text, string layoutType)
        {
            if (string.IsNullOrEmpty(text))
                return new string[0];

            if (layoutType == "mqseries")
                return SplitTextIntoFixedLengthLines(text, 600);
            else if (layoutType == "idoc")
                return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            else
                return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private string[] SplitTextIntoFixedLengthLines(string text, int lineLength)
        {
            if (string.IsNullOrEmpty(text) || lineLength <= 0)
                return new string[0];

            int totalLines = (int)Math.Ceiling((double)text.Length / lineLength);
            string[] lines = new string[totalLines];

            for (int i = 0; i < totalLines; i++)
            {
                int startIndex = i * lineLength;
                int length = Math.Min(lineLength, text.Length - startIndex);
                lines[i] = text.Substring(startIndex, length);

                if (length < lineLength)
                {
                    lines[i] = lines[i].PadRight(lineLength);
                }

                string sequence = lines[i].Substring(0, Math.Min(6, lines[i].Length));
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "SplitTextIntoFixedLengthLines",
                    Level = "Info",
                    Message = $"Linha {i + 1}: Sequência '{sequence}', Tamanho: {lines[i].Length}"
                });
            }

            return lines;
        }
    }
}


