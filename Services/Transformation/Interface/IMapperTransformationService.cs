using LayoutParserApi.Services.Transformation.Models;

namespace LayoutParserApi.Services.Transformation.Interface
{
    /// <summary>
    /// Contrato do Pathway 1 de transformação - <b>legado</b>, sem novo investimento
    /// (item 2.1 do dispatch de IA, docs/architecture/ai-roadmap-dispatch.md, 2026-07-21).
    /// </summary>
    public interface IMapperTransformationService
    {
        Task<TransformationResult> TransformAsync(string inputText, string inputLayoutGuid, string targetLayoutGuid, string mapperXml = null);
    }
}
