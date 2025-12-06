using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;

namespace LayoutParserApi.Services.Interfaces
{
    public interface ILayoutParserService
    {
        DocumentStructure BuildDocumentStructure(ParsingResult result);
        Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream);

        Layout ReordenarSequences(Layout layout);
        Layout ReestruturarLayout(Layout layoutOriginal);
        
        /// <summary>
        /// Calcula validações e posições dos campos para cada linha do layout
        /// </summary>
        /// <param name="layout">Layout a ser validado</param>
        /// <param name="expectedLineLength">Tamanho esperado da linha (padrão: 600)</param>
        List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength = 600);
        
        /// <summary>
        /// Parseia XML do layout para objeto Layout (sem precisar de arquivo txt)
        /// </summary>
        Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent);
    }
}