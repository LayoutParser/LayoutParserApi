using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Models.Enums;
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

        /// <summary>
        /// Split canônico: decide pelo FORMATO FÍSICO resolvido do layout (ADR-001), não por
        /// <c>LayoutType</c> nem por heurística de conteúdo.
        /// </summary>
        public string[] SplitTextIntoLines(string text, PositionalFormat format, int lineLength = LineLengthResolver.LegacyDefaultLineLength)
        {
            if (string.IsNullOrEmpty(text))
                return new string[0];

            if (format == PositionalFormat.ContinuousStream)
            {
                // MQSeries: stream contínuo fatiado a cada N chars
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "SplitTextIntoLines",
                    Level = "Info",
                    Message = $"Usando split de stream contínuo ({lineLength} chars) - formato: {format}"
                });
                return SplitTextIntoFixedLengthLines(text, lineLength);
            }

            // IDOC: um registro por linha física
            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "SplitTextIntoLines",
                Level = "Info",
                Message = $"Usando split por quebras de linha - formato: {format}"
            });
            return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Sobrecarga LEGADA por string de tipo (ver <see cref="ILineSplitter"/>). Traduz a string
        /// para <see cref="PositionalFormat"/> preservando o comportamento histórico e delega.
        /// </summary>
        public string[] SplitTextIntoLines(string text, string layoutType, int lineLength = LineLengthResolver.LegacyDefaultLineLength)
        {
            if (string.IsNullOrEmpty(text))
                return new string[0];

            // Layout posicional de largura fixa (TextPositional ou mqseries)
            if (layoutType == "mqseries" || layoutType == "TextPositional")
                return SplitTextIntoLines(text, PositionalFormat.ContinuousStream, lineLength);

            if (layoutType != "idoc")
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "SplitTextIntoLines",
                    Level = "Warn",
                    Message = $"Tipo de layout desconhecido: {layoutType}. Usando split por quebras de linha."
                });
            }

            return SplitTextIntoLines(text, PositionalFormat.RecordPerLine, lineLength);
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