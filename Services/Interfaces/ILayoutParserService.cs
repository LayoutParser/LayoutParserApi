using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Structure;

namespace LayoutParserApi.Services.Interfaces
{
    public interface ILayoutParserService
    {
        DocumentStructure BuildDocumentStructure(ParsingResult result);
        Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream);

        Layout ReordenarSequences(Layout layout);
        Layout ReestruturarLayout(Layout layoutOriginal);
    }
}