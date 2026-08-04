using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Models.Enums;

namespace LayoutParserApi.Services.Parsing.Interfaces
{
    public interface ILineSplitter
    {
        /// <summary>
        /// Divide o documento em registros conforme o formato FÍSICO resolvido do layout.
        /// Esta é a sobrecarga canônica — ver <see cref="PositionalFormatResolver"/> e ADR-001.
        /// </summary>
        /// <param name="lineLength">Tamanho da linha para layouts posicionais (default legado, ver <see cref="LineLengthResolver"/>)</param>
        string[] SplitTextIntoLines(string text, PositionalFormat format, int lineLength = LineLengthResolver.LegacyDefaultLineLength);

        /// <summary>
        /// Sobrecarga LEGADA, por string de tipo. Mantida apenas para chamadores antigos: ela não
        /// distingue MQSeries de IDOC dentro de <c>TextPositional</c>, que é exatamente a origem do
        /// defeito descrito na ADR-001. Prefira a sobrecarga com <see cref="PositionalFormat"/>.
        /// </summary>
        /// <param name="lineLength">Tamanho da linha para layouts posicionais (default legado, ver <see cref="LineLengthResolver"/>)</param>
        string[] SplitTextIntoLines(string text, string layoutType, int lineLength = LineLengthResolver.LegacyDefaultLineLength);
    }
}
