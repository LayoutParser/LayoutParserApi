using LayoutParserApi.Models.Configuration;

namespace LayoutParserApi.Services.Parsing.Interfaces
{
    public interface ILineSplitter
    {
        /// <param name="lineLength">Tamanho da linha para layouts posicionais (default legado, ver <see cref="LineLengthResolver"/>)</param>
        string[] SplitTextIntoLines(string text, string layoutType, int lineLength = LineLengthResolver.LegacyDefaultLineLength);
    }
}
